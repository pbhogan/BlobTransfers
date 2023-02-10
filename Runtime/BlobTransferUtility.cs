using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;


namespace GallantGames.NetCode
{
	[BurstCompile]
	public static class BlobTransferUtility
	{
		[BurstCompile]
		public static NativeArray<byte> CreateRandomBytes( int size, uint seed = 0 )
		{
			var random = new Random( seed );
			var bytes = new NativeArray<byte>( size, Allocator.Persistent );
			for (var i = 0; i < size; i++) bytes[i] = (byte) random.NextInt( 0, 255 );
			return bytes;
		}


		[BurstCompile]
		public static uint CalculateCRC32( NativeArray<byte>.ReadOnly bytes )
		{
			var crc = 0xFFFFFFFF;
			var n = bytes.Length;
			for (var i = 0; i < n; i++)
			{
				var b = bytes[i];
				for (var j = 0; j < 8; j++)
				{
					var x = (b ^ crc) & 1;
					crc >>= 1;
					if (x != 0) crc ^= 0xEDB88320;
					b >>= 1;
				}
			}

			return crc;
		}
	}
}
