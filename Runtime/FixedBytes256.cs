using System.Runtime.InteropServices;


namespace GallantGames.NetCode
{
	[StructLayout( LayoutKind.Explicit, Size = 256 )]
	public struct FixedBytes256
	{
		[FieldOffset( 0 )]
		public FixedBytes16 Offset000;
		[FieldOffset( 16 )]
		public FixedBytes16 Offset016;
		[FieldOffset( 32 )]
		public FixedBytes16 Offset032;
		[FieldOffset( 48 )]
		public FixedBytes16 Offset048;
		[FieldOffset( 64 )]
		public FixedBytes16 Offset064;
		[FieldOffset( 80 )]
		public FixedBytes16 Offset080;
		[FieldOffset( 96 )]
		public FixedBytes16 Offset096;
		[FieldOffset( 112 )]
		public FixedBytes16 Offset112;
		[FieldOffset( 128 )]
		public FixedBytes16 Offset128;
		[FieldOffset( 144 )]
		public FixedBytes16 Offset144;
		[FieldOffset( 160 )]
		public FixedBytes16 Offset160;
		[FieldOffset( 176 )]
		public FixedBytes16 Offset176;
		[FieldOffset( 192 )]
		public FixedBytes16 Offset192;
		[FieldOffset( 208 )]
		public FixedBytes16 Offset208;
		[FieldOffset( 224 )]
		public FixedBytes16 Offset224;
		[FieldOffset( 240 )]
		public FixedBytes16 Offset240;


		public unsafe void* GetUnsafePtr()
		{
			fixed (void* ptr = &Offset000)
			{
				return ptr;
			}
		}
	}
}
