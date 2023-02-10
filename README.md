# Experimental BLOB Transfers (for Unity Netcode for Entities)

This is a proof-of-concept workaround using RPCs to transfer Binary Large Objects (`NativeArray<byte>`) between the
client
and server.

```csharp
var bytes = BlobTransferUtility.CreateRandomBytes( 
    64 * 1024, 
    (uint) (SystemAPI.Time.ElapsedTime * 1000) 
);
Log.Info( 
    "Creating blob transfer: CRC32 = {crc:X8}", 
    BlobTransferUtility.CalculateCRC32( bytes.AsReadOnly() ) 
);
ecb.AddComponent( 
    ecb.CreateEntity(), 
    new CreateBlobTransfer { 
        TargetConnection = clientConnection, 
        Blob = bytes 
    } 
);
```

`BlobTransferUtility` is included in this package for testing.

To initiate a transfer, create a new entity with a `CreateBlobTransfer` component on it. `TargetConnection` is the
connection to send the blob to, and `Blob` is the `NativeArray<byte>` data to send.

**NOTE:** The system will take ownership of the native array and will dispose it later. If you need to hold onto the
data yourself, make a copy to assign to the `CreateBlobTransfer` component.

If you want to track progress of the transfer as the sender, you can keep a reference to the entity you created.
The `CreateBlobTransfer` will be removed and a new `OutgoingBlobTransfer` component will be added to replace it. This
component will contain progress data:

```csharp
public struct OutgoingBlobTransfer : IComponentData
{
    public float ElapsedTime;
    public int TotalBytes;
    public int BytesSent;
    public bool IsComplete => BytesSent >= TotalBytes;
    public bool InProgress => BytesSent < TotalBytes;
    public float Progress => math.clamp( BytesSent / (float) TotalBytes, 0f, 1f );
}
```

**NOTE:** Once the transfer is complete, the entity and component will be deleted, and the internal state (including the
byte data) disposed after a few ticks delay.

You can delete this entity at any time to cancel the transfer.

You can use the `OutgoingBlobTransfer` component to track progress:

```csharp
foreach (var (transfer, entity) in SystemAPI.Query<RefRO<OutgoingBlobTransfer>>().WithEntityAccess())
{
    ref readonly var outgoingTransfer = ref transfer.ValueRO;
    Log.Info( 
        "Outgoing transfer progress: {progress:0.0}%", 
        outgoingTransfer.Progress * 100 
    );
    if (outgoingTransfer.IsComplete)
    {
        Log.Info( "Outgoing transfer is complete." );
        ecb.DestroyEntity( entity ); // Optional, but good practice.
    }
}
```

On the receiver side, an entity will be created with a `IncomingBlobTransfer` component:

```csharp
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
```

**NOTE:** Once the transfer is complete, you are expected to destroy the entity. If you fail to do so, it will be
automatically destroyed after a few ticks and an error will be logged in debug mode. The blob will be disposed, so make
a copy if you need to keep the data.

You can delete this entity at any time to cancel the transfer.

You can use the `IncomingBlobTransfer` component to track progress:

```csharp
foreach (var (transfer, entity) in SystemAPI.Query<RefRO<IncomingBlobTransfer>>().WithEntityAccess())
{
    ref readonly var incomingTransfer = ref transfer.ValueRO;
    Log.Info( 
        "Incoming transfer progress: {progress:0.0}%", 
        incomingTransfer.Progress * 100 
    );
    if (incomingTransfer.IsComplete)
    {
        Log.Info( 
            "Incoming transfer is complete: CRC32 = {crc:X8}", 
            BlobTransferUtility.CalculateCRC32( incomingTransfer.Blob )
        );
        ecb.DestroyEntity( entity ); // Required
    }
}
```

## Additional Notes

* The system handling transfers is `BlobTransferSystem` and contains a few constants:
    * `maxTransferAgeSinceCompletion` defines how many ticks to wait before a transfer is automatically cleaned up after
      completion.
    * `maxTransferBytesPerSecond` defines how many bytes can be sent across all blob transfers combined.
* Transfers are sent via RPCs in chunks of up to 256 bytes maximum.
* Blob transfers will not work with thin clients.