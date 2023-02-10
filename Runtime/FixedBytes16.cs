using System.Runtime.InteropServices;


namespace GallantGames.NetCode
{
	[StructLayout( LayoutKind.Explicit, Size = 16 )]
	public struct FixedBytes16
	{
		[FieldOffset( 0 )]
		public byte Offset00;
		[FieldOffset( 1 )]
		public byte Offset01;
		[FieldOffset( 2 )]
		public byte Offset02;
		[FieldOffset( 3 )]
		public byte Offset03;
		[FieldOffset( 4 )]
		public byte Offset04;
		[FieldOffset( 5 )]
		public byte Offset05;
		[FieldOffset( 6 )]
		public byte Offset06;
		[FieldOffset( 7 )]
		public byte Offset07;
		[FieldOffset( 8 )]
		public byte Offset08;
		[FieldOffset( 9 )]
		public byte Offset09;
		[FieldOffset( 10 )]
		public byte Offset10;
		[FieldOffset( 11 )]
		public byte Offset11;
		[FieldOffset( 12 )]
		public byte Offset12;
		[FieldOffset( 13 )]
		public byte Offset13;
		[FieldOffset( 14 )]
		public byte Offset14;
		[FieldOffset( 15 )]
		public byte Offset15;
	}
}
