#if (UNITY_EDITOR || DEVELOPMENT_BUILD) && !NETCODE_NDEBUG
#define NETCODE_DEBUG
#define NETCODE_DEBUG_VERBOSE
// #define NETCODE_DEBUG_CHUNKS
#endif

using System;
using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Logging;
using Unity.Mathematics;
using Unity.NetCode;


namespace GallantGames.NetCode
{
	public struct CreateBlobTransfer : IComponentData
	{
		public Entity TargetConnection;
		public NativeArray<byte> Blob;
	}


	public struct OutgoingBlobTransfer : IComponentData
	{
		public float ElapsedTime;
		public int TotalBytes;
		public int BytesSent;
		public bool IsComplete => BytesSent >= TotalBytes;
		public bool InProgress => BytesSent < TotalBytes;
		public float Progress => math.clamp( BytesSent / (float) TotalBytes, 0f, 1f );
	}


	public struct OutgoingBlobTransferCleanup : ICleanupComponentData {}


	public struct CancelOutgoingBlobTransfer : IRpcCommand
	{
		public uint4 TransferId;
	}


	public struct IncomingBlobTransfer : IComponentData
	{
		public float ElapsedTime;
		public NativeArray<byte>.ReadOnly Blob;
		public int BytesReceived;
		public int TotalBytes => Blob.Length;
		public bool IsComplete => BytesReceived >= TotalBytes;
		public bool InProgress => BytesReceived < TotalBytes;
		public float Progress => math.clamp( BytesReceived / (float) TotalBytes, 0f, 1f );
	}


	public struct IncomingBlobTransferCleanup : ICleanupComponentData
	{
		public uint4 TransferId;
	}


	public struct CancelIncomingBlobTransfer : IRpcCommand
	{
		public uint4 TransferId;
	}


	public struct BlobTransferChunk : IRpcCommand
	{
		public uint4 TransferId;
		public int TotalBytes;
		public int Offset;
		public int Length;
		public FixedBytes256 Bytes;
	}


	[BurstCompile]
	[UpdateInGroup( typeof(SimulationSystemGroup), OrderFirst = true )]
	[UpdateAfter( typeof(BeginSimulationEntityCommandBufferSystem) )]
	[WorldSystemFilter( WorldSystemFilterFlags.ServerSimulation | WorldSystemFilterFlags.ClientSimulation )]
	public partial struct BlobTransferSystem : ISystem
	{
		const int maxTransferChunkSize = 256; // Note: Must match the size of BlobTransferChunk.Bytes
		const int maxTransferAgeSinceCompletion = 4;
		const int maxTransferBytesPerSecond = 32 * 1024;


		struct InternalOutgoingTransferState : IDisposable
		{
			public uint4 TransferId;
			public Entity TransferEntity;
			public Entity TargetConnection;
			public NativeArray<byte> Blob;
			public double StartTime;
			public int BytesSent;
			public int TotalBytes => Blob.Length;
			public bool IsComplete => BytesSent >= TotalBytes;
			public bool InProgress => BytesSent < TotalBytes;
			public ushort AgeSinceComplete;


			public void Dispose()
			{
				Blob.Dispose();
			}
		}


		struct InternalIncomingTransferState : IDisposable
		{
			public uint4 TransferId;
			public Entity TransferEntity;
			public Entity SourceConnection;
			public NativeArray<byte> Blob;
			public double StartTime;
			public int BytesReceived;
			public int TotalBytes => Blob.Length;
			public bool IsComplete => BytesReceived >= TotalBytes;
			public bool InProgress => BytesReceived < TotalBytes;
			public ushort AgeSinceComplete;


			public void Dispose()
			{
				Blob.Dispose();
			}
		}


		NativeList<InternalOutgoingTransferState> outgoingTransfers;
		NativeList<InternalIncomingTransferState> incomingTransfers;
		NativeHashSet<uint4> canceledTransfers;
		EntityQuery outgoingTransferQuery;
		int totalOutgoingQuota;


		[BurstCompile]
		public void OnCreate( ref SystemState state )
		{
			outgoingTransfers = new NativeList<InternalOutgoingTransferState>( 16, Allocator.Persistent );
			incomingTransfers = new NativeList<InternalIncomingTransferState>( 16, Allocator.Persistent );
			canceledTransfers = new NativeHashSet<uint4>( 16, Allocator.Persistent );
			outgoingTransferQuery = SystemAPI.QueryBuilder().WithAll<OutgoingBlobTransfer>().Build();
		}


		[BurstCompile]
		public void OnDestroy( ref SystemState state )
		{
			foreach (var transferState in outgoingTransfers) transferState.Dispose();
			foreach (var transferState in incomingTransfers) transferState.Dispose();
			outgoingTransfers.Dispose();
			incomingTransfers.Dispose();
			canceledTransfers.Dispose();
		}


		[BurstCompile]
		public void OnUpdate( ref SystemState state )
		{
			var currentTime = SystemAPI.Time.ElapsedTime;

			#if NETCODE_DEBUG
			var worldName = state.WorldUnmanaged.Name;
			#endif

			var ecb = SystemAPI.GetSingletonRW<EndSimulationEntityCommandBufferSystem.Singleton>().ValueRW.CreateCommandBuffer( state.WorldUnmanaged );

			// This is how blob transfers start.
			// Upgrade CreateBlobTransfer components to OutgoingBlobTransfer.
			foreach (var (createBlobTransfer, entity) in SystemAPI.Query<CreateBlobTransfer>()
			                                                      .WithEntityAccess())
			{
				// First check that the blob is actually valid and worth sending.
				if (createBlobTransfer.Blob is { IsCreated: true, Length: > 0 })
				{
					// Add the OutgoingBlobTransfer component. It only exposes progress.
					ecb.AddComponent( entity, new OutgoingBlobTransfer { TotalBytes = createBlobTransfer.Blob.Length } );

					// Transfer IDs are a GUID converted to uint4 to be burst compatible.
					var transferId = GenerateTransferId();

					// We take possession of the blob and keep it in internal state.
					outgoingTransfers.Add( new InternalOutgoingTransferState
					{
						TransferId = transferId,
						TransferEntity = entity,
						TargetConnection = createBlobTransfer.TargetConnection,
						Blob = createBlobTransfer.Blob,
						StartTime = currentTime
					} );

					// The cleanup component will be used to dispose of the internal state (and blob) after completion.
					ecb.AddComponent<OutgoingBlobTransferCleanup>( entity );

					// And, finally, remove the request component.
					ecb.RemoveComponent<CreateBlobTransfer>( entity );
				}
				else
				{
					#if NETCODE_DEBUG
					Log.Error(
						"In {world}, blob is invalid for {type} {entity}",
						worldName,
						nameof(CreateBlobTransfer),
						entity.ToFixedString()
					);
					#endif

					// This transfer request has an invalid blob, so just destroy it and move on.
					ecb.DestroyEntity( entity );
				}
			}

			// How many outgoing transfers are there? This is approximate, because some may be complete.
			var outgoingTransferCount = outgoingTransferQuery.CalculateEntityCount();

			// Only accumulate total quota if there's actually something to send.
			totalOutgoingQuota = outgoingTransferCount == 0 ? 0 : totalOutgoingQuota + (int) math.ceil( SystemAPI.Time.DeltaTime * maxTransferBytesPerSecond );

			// Figure out how much we can spend per transfer (avoid divide by zero).
			var outgoingQuotaPerTransfer = totalOutgoingQuota / math.max( outgoingTransferCount, 1 );

			// Handle all the outgoing transfers.
			foreach (var (outgoingBlobTransfer, entity) in SystemAPI.Query<RefRW<OutgoingBlobTransfer>>()
			                                                        .WithEntityAccess())
			{
				var index = FindOutgoingTransferIndex( entity );
				if (index < 0)
				{
					#if NETCODE_DEBUG
					Log.Error(
						"In {world}, could not find internal state for {type} {entity}",
						worldName,
						nameof(OutgoingBlobTransfer),
						entity.ToFixedString()
					);
					#endif

					// This should never happen but clean up the mess as best we can.
					ecb.DestroyEntity( entity );
					continue;
				}

				ref var transferState = ref outgoingTransfers.ElementAt( index );

				// Check that the target connection is still around.
				// QUESTION: Is there a better way to check this than simply entity existence?
				if (!state.EntityManager.Exists( transferState.TargetConnection ))
				{
					#if NETCODE_DEBUG
					Log.Error(
						"In {world}, TargetConnection no longer exists for {type} {entity}",
						worldName,
						nameof(OutgoingBlobTransfer),
						entity.ToFixedString()
					);
					#endif

					ecb.DestroyEntity( entity );
					continue;
				}

				if (transferState.InProgress)
				{
					// This transfer is still in progress.
					outgoingBlobTransfer.ValueRW.ElapsedTime = (float) (currentTime - transferState.StartTime);

					// We have a minimum quota to avoid spamming lots of tiny packets.
					if (outgoingQuotaPerTransfer > 32)
					{
						var transferQuota = outgoingQuotaPerTransfer;

						while (transferQuota > 0 &&
						       transferState.InProgress)
						{
							var maxChunkSize = math.min( transferQuota, maxTransferChunkSize );

							var rpcEntity = ecb.CreateEntity();
							var transferChunk = new BlobTransferChunk { TransferId = transferState.TransferId, TotalBytes = transferState.TotalBytes };
							var chunkSize = CopyBytesToChunk( transferState.Blob, ref transferChunk, transferState.BytesSent, maxChunkSize );
							ecb.AddComponent( rpcEntity, transferChunk );
							ecb.AddComponent( rpcEntity, new SendRpcCommandRequestComponent { TargetConnection = transferState.TargetConnection } );

							#if NETCODE_DEBUG && NETCODE_DEBUG_CHUNKS
							Log.Info(
								">>> OUTGOING CHUNK: Offset = {0}, Length = {1}, TransferId = {2:X8}-{3:X8}-{4:X8}-{5:X8}",
								transferChunk.Offset,
								transferChunk.Length,
								transferState.TransferId.x, transferState.TransferId.y, transferState.TransferId.z, transferState.TransferId.w
							);
							#endif

							// Update progress on the internal state and the transfer component.
							transferState.BytesSent += chunkSize;
							outgoingBlobTransfer.ValueRW.BytesSent = transferState.BytesSent;

							// Update quotas; 
							transferQuota -= chunkSize;
							totalOutgoingQuota -= chunkSize;
						}
					}

					continue;
				}

				// This outgoing transfer is complete.
				// Ideally, user will clean it up but, if not, keep it around for a few ticks and then destroy it.
				if (++transferState.AgeSinceComplete >= maxTransferAgeSinceCompletion)
				{
					#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
					Log.Warning(
						"In {world}, {type} {entity} has not been destroyed for {age} frames!",
						worldName,
						nameof(OutgoingBlobTransfer),
						entity.ToFixedString(),
						transferState.AgeSinceComplete
					);
					#endif

					ecb.DestroyEntity( entity );
				}
			}

			// Handle outgoing transfer cleanup.
			foreach (var (_, entity) in SystemAPI.Query<OutgoingBlobTransferCleanup>()
			                                     .WithNone<OutgoingBlobTransfer>()
			                                     .WithEntityAccess())
			{
				#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
				Log.Info(
					"In {world}, removing {type} {entity}",
					worldName,
					nameof(OutgoingBlobTransferCleanup),
					entity.ToFixedString()
				);
				#endif

				var index = FindOutgoingTransferIndex( entity );
				if (index > -1)
				{
					ref var transferState = ref outgoingTransfers.ElementAt( index );

					if (transferState.InProgress)
					{
						// Send a cancel command to the target (incoming from their perspective).
						var rpcEntity = ecb.CreateEntity();
						ecb.AddComponent( rpcEntity, new CancelIncomingBlobTransfer { TransferId = transferState.TransferId } );
						ecb.AddComponent( rpcEntity, new SendRpcCommandRequestComponent { TargetConnection = transferState.TargetConnection } );
					}

					// Dispose of the internal transfer state.
					transferState.Dispose();
					outgoingTransfers.RemoveAtSwapBack( index );
				}

				// And we're done.
				ecb.RemoveComponent<OutgoingBlobTransferCleanup>( entity );
				ecb.DestroyEntity( entity );
			}


			// Handle explicit outgoing transfer cancellations.
			foreach (var (_, cancelTransfer, rpcEntity) in SystemAPI.Query<
				                                                        RefRO<ReceiveRpcCommandRequestComponent>,
				                                                        RefRO<CancelOutgoingBlobTransfer>>()
			                                                        .WithEntityAccess())
			{
				var transferId = cancelTransfer.ValueRO.TransferId;

				#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
				Log.Info(
					"In {world}, canceling outgoing transfer {1:X8}-{2:X8}-{3:X8}-{4:X8}",
					worldName,
					transferId.x, transferId.y, transferId.z, transferId.w
				);
				#endif

				var index = FindOutgoingTransferIndex( transferId );
				if (index > -1)
				{
					ref var transferState = ref outgoingTransfers.ElementAt( index );

					// Dispose of the internal transfer state.
					transferState.Dispose();
					outgoingTransfers.RemoveAtSwapBack( index );

					// Remove the cleanup component so we don't trigger a second cleanup.
					ecb.RemoveComponent<OutgoingBlobTransferCleanup>( transferState.TransferEntity );
					ecb.DestroyEntity( transferState.TransferEntity );
				}

				ecb.DestroyEntity( rpcEntity );
			}


			// Handle incoming transfer chunks.
			foreach (var (rpcSource, transferChunk, rpcEntity) in SystemAPI.Query<
				                                                               RefRO<ReceiveRpcCommandRequestComponent>,
				                                                               RefRO<BlobTransferChunk>>()
			                                                               .WithEntityAccess())
			{
				var transferId = transferChunk.ValueRO.TransferId;

				#if NETCODE_DEBUG && NETCODE_DEBUG_CHUNKS
				Log.Info(
					"<<< INCOMING CHUNK: Length = {0}, TransferId = {1:X8}-{2:X8}-{3:X8}-{4:X8}",
					transferChunk.ValueRO.Length,
					transferId.x, transferId.y, transferId.z, transferId.w
				);
				#endif

				var index = FindIncomingTransferIndex( transferId );
				if (index < 0)
				{
					// Since we can't find the internal transfer state, this is a new transfer.
					// But we need to check that it's not missing due to being prematurely canceled.
					if (canceledTransfers.Contains( transferId ))
					{
						// If it is for a canceled transfer, then delete it.
						ecb.DestroyEntity( rpcEntity );
					}
					else
					{
						#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
						Log.Info(
							"In {world}, creating new incoming transfer for TransferId = {1:X8}-{2:X8}-{3:X8}-{4:X8}",
							worldName,
							transferId.x, transferId.y, transferId.z, transferId.w
						);
						#endif

						// Create a new blob and copy the chunk intro it.
						var blob = new NativeArray<byte>( transferChunk.ValueRO.TotalBytes, Allocator.Persistent, NativeArrayOptions.ClearMemory );
						CopyBytesToArray( transferChunk, blob );

						// Create new internal transfer state and the incoming transfer component which exposes it.
						incomingTransfers.Add( new InternalIncomingTransferState
						{
							TransferId = transferId,
							TransferEntity = rpcEntity,
							SourceConnection = rpcSource.ValueRO.SourceConnection,
							Blob = blob,
							BytesReceived = transferChunk.ValueRO.Length,
							StartTime = currentTime
						} );
						var incomingBlobTransfer = new IncomingBlobTransfer
						{
							Blob = blob.AsReadOnly(),
							BytesReceived = transferChunk.ValueRO.Length
						};
						ecb.AddComponent( rpcEntity, incomingBlobTransfer );
						ecb.AddComponent( rpcEntity, new IncomingBlobTransferCleanup { TransferId = transferId } );

						// We are reusing the existing RPC entity for expediency, so we just remove the RPC components.
						// This avoids the need to deal with a placeholder entity created through the ECB.
						ecb.RemoveComponent<ReceiveRpcCommandRequestComponent>( rpcEntity );
						ecb.RemoveComponent<BlobTransferChunk>( rpcEntity );
					}
				}
				else
				{
					// We have internal transfer state, so this is an existing transfer.
					ref var transferState = ref incomingTransfers.ElementAt( index );

					// Update the incoming transfer component.
					// It's possible that more than one packet arrived on the tick in which the IncomingBlobTransfer
					// gets created and due to deferring with the ECB that it doesn't exist on the entity yet.
					// If not, just defer doing anything with the chunk until the next tick.
					if (SystemAPI.HasComponent<IncomingBlobTransfer>( transferState.TransferEntity ))
					{
						// Copy the chunk into the blob.
						CopyBytesToArray( transferChunk, transferState.Blob );
						transferState.BytesReceived += transferChunk.ValueRO.Length;

						// Update the incoming transfer component.
						var incomingBlobTransfer = SystemAPI.GetComponent<IncomingBlobTransfer>( transferState.TransferEntity );
						incomingBlobTransfer.BytesReceived = transferState.BytesReceived;
						incomingBlobTransfer.ElapsedTime = (float) (currentTime - transferState.StartTime);
						ecb.SetComponent( transferState.TransferEntity, incomingBlobTransfer );

						// Finally, destroy the RPC chunk entity.
						ecb.DestroyEntity( rpcEntity );
					}

					#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
					if (transferState.IsComplete)
					{
						var elapsedTime = currentTime - transferState.StartTime;
						Log.Info(
							"In {world}, incoming transfer completed in {elapsed:0.0} seconds ({kbps:0.0} kB/s)",
							worldName,
							elapsedTime,
							transferState.TotalBytes / math.max( elapsedTime * 1024, math.EPSILON )
						);
					}
					#endif
				}
			}


			// Handle incoming transfer lifecycle. 
			foreach (var (_, entity) in SystemAPI.Query<IncomingBlobTransfer>()
			                                     .WithEntityAccess())
			{
				var index = FindIncomingTransferIndex( entity );
				if (index < 0)
				{
					#if NETCODE_DEBUG
					Log.Error(
						"In {world}, could not find internal state for {type} {entity}",
						worldName,
						nameof(IncomingBlobTransfer),
						entity.ToFixedString()
					);
					#endif
					ecb.DestroyEntity( entity );
					continue;
				}

				ref var transferState = ref incomingTransfers.ElementAt( index );

				if (!state.EntityManager.Exists( transferState.SourceConnection ))
				{
					#if NETCODE_DEBUG
					Log.Error(
						"In {world}, SourceConnection no longer exists for {type} {entity}",
						worldName,
						nameof(IncomingBlobTransfer),
						entity.ToFixedString()
					);
					#endif

					ecb.DestroyEntity( entity );
					continue;
				}

				// We expect the user to consume (destroy) the incoming transfer within a few ticks.
				if (transferState.IsComplete &&
				    ++transferState.AgeSinceComplete >= maxTransferAgeSinceCompletion)
				{
					#if NETCODE_DEBUG
					Log.Error(
						"In {world}, {type} {entity} has not been destroyed for {age} frames!",
						worldName,
						nameof(IncomingBlobTransfer),
						entity.ToFixedString(),
						transferState.AgeSinceComplete
					);
					#endif

					ecb.DestroyEntity( entity );
				}
			}


			// Handle incoming transfer cleanup.
			foreach (var (incomingTransferCleanup, entity) in SystemAPI.Query<IncomingBlobTransferCleanup>()
			                                                           .WithNone<IncomingBlobTransfer>()
			                                                           .WithEntityAccess())
			{
				#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
				Log.Info(
					"In {world}, removing {type} {entity}",
					worldName,
					nameof(IncomingBlobTransferCleanup),
					entity.ToFixedString()
				);
				#endif

				var index = FindIncomingTransferIndex( entity );
				if (index > -1)
				{
					ref var transferState = ref incomingTransfers.ElementAt( index );
					if (transferState.InProgress)
					{
						// We need to handle the situation where the user deletes the incoming transfer to cancel it
						// before it is complete. So, keep track of the transfer ID and further incoming chunks should
						// be discarded. Is there a better way to handle this? It should be fairly rare.
						canceledTransfers.Add( transferState.TransferId );

						// Send a cancel command to the source (outgoing from their perspective).
						var rpcEntity = ecb.CreateEntity();
						ecb.AddComponent( rpcEntity, new CancelOutgoingBlobTransfer { TransferId = transferState.TransferId } );
						ecb.AddComponent( rpcEntity, new SendRpcCommandRequestComponent { TargetConnection = transferState.SourceConnection } );
					}

					// Dispose of the internal transfer state.
					transferState.Dispose();
					incomingTransfers.RemoveAtSwapBack( index );
				}
				else
				{
					// Edge case: internal state doesn't exist. Maybe it was disposed elsewhere.
					// Record it as canceled because we don't know if it was completed.
					canceledTransfers.Add( incomingTransferCleanup.TransferId );
				}

				// And we're done.
				ecb.RemoveComponent<IncomingBlobTransferCleanup>( entity );
				ecb.DestroyEntity( entity );
			}


			// Handle explicit incoming transfer cancellations.
			foreach (var (_, cancelTransfer, rpcEntity) in SystemAPI.Query<
				                                                        RefRO<ReceiveRpcCommandRequestComponent>,
				                                                        RefRO<CancelIncomingBlobTransfer>>()
			                                                        .WithEntityAccess())
			{
				var transferId = cancelTransfer.ValueRO.TransferId;

				#if NETCODE_DEBUG && NETCODE_DEBUG_VERBOSE
				Log.Info(
					"In {world}, canceling incoming transfer {1:X8}-{2:X8}-{3:X8}-{4:X8}",
					worldName,
					transferId.x, transferId.y, transferId.z, transferId.w
				);
				#endif

				var index = FindIncomingTransferIndex( transferId );
				if (index > -1)
				{
					ref var transferState = ref incomingTransfers.ElementAt( index );

					// If it was still in progress, record the canceled transfer so that
					// further incoming chunks don't recreate it.
					if (transferState.InProgress)
					{
						canceledTransfers.Add( transferState.TransferId );
					}

					// Dispose of the internal transfer state.
					transferState.Dispose();
					incomingTransfers.RemoveAtSwapBack( index );

					// Remove the cleanup component so we don't trigger a second cleanup.
					ecb.RemoveComponent<IncomingBlobTransferCleanup>( transferState.TransferEntity );
					ecb.DestroyEntity( transferState.TransferEntity );
				}

				ecb.DestroyEntity( rpcEntity );
			}
		}


		int FindOutgoingTransferIndex( Entity transferEntity )
		{
			for (var index = 0; index < outgoingTransfers.Length; index++)
			{
				if (outgoingTransfers.ElementAt( index ).TransferEntity.Equals( transferEntity ))
				{
					return index;
				}
			}

			return -1;
		}


		int FindOutgoingTransferIndex( uint4 transferId )
		{
			for (var index = 0; index < outgoingTransfers.Length; index++)
			{
				if (outgoingTransfers.ElementAt( index ).TransferId.Equals( transferId ))
				{
					return index;
				}
			}

			return -1;
		}


		int FindIncomingTransferIndex( Entity transferEntity )
		{
			for (var index = 0; index < incomingTransfers.Length; index++)
			{
				if (incomingTransfers.ElementAt( index ).TransferEntity.Equals( transferEntity ))
				{
					return index;
				}
			}

			return -1;
		}


		int FindIncomingTransferIndex( uint4 transferId )
		{
			for (var index = 0; index < incomingTransfers.Length; index++)
			{
				if (incomingTransfers.ElementAt( index ).TransferId.Equals( transferId ))
				{
					return index;
				}
			}

			return -1;
		}


		static unsafe uint4 GenerateTransferId()
		{
			var guid = Guid.NewGuid();
			return *(uint4*) &guid;
		}


		static unsafe int CopyBytesToChunk( NativeArray<byte> source, ref BlobTransferChunk target, int offset, int maxLength )
		{
			var length = math.min( source.Length - offset, maxLength );

			fixed (void* targetPtr = &target.Bytes)
			{
				UnsafeUtility.MemCpy( targetPtr, (byte*) source.GetUnsafeReadOnlyPtr() + offset, length );
			}

			target.Offset = offset;
			target.Length = length;

			return length;
		}


		static unsafe void CopyBytesToArray( RefRO<BlobTransferChunk> source, NativeArray<byte> target )
		{
			fixed (void* sourcePtr = &source.ValueRO.Bytes)
			{
				UnsafeUtility.MemCpy( (byte*) target.GetUnsafePtr() + source.ValueRO.Offset, sourcePtr, source.ValueRO.Length );
			}
		}
	}
}
