namespace GenesisSharp.Tests;

/// <summary>Hand-assembles the Z80 opcodes the unit tests need, straight from the bit-field
/// formulas — same rationale as GenesisSharp.Tests.Asm for the 68000. r-index order: 0=B,
/// 1=C, 2=D, 3=E, 4=H, 5=L, 6=(HL), 7=A.</summary>
internal static class Z80Asm
{
    public static byte LdRR(int dst, int src) => (byte)(0x40 | (dst << 3) | src);
    public static byte AluR(int op, int src) => (byte)(0x80 | (op << 3) | src);
    public static byte AluImm(int op) => (byte)(0xC6 | (op << 3));
    public static byte IncR(int r) => (byte)(0x04 | (r << 3));
    public static byte DecR(int r) => (byte)(0x05 | (r << 3));
    public static byte LdRImm(int r) => (byte)(0x06 | (r << 3));
    public static byte LdRpImm(int p) => (byte)(0x01 | (p << 4));
    public static byte IncRp(int p) => (byte)(0x03 | (p << 4));
    public static byte DecRp(int p) => (byte)(0x0B | (p << 4));
    public static byte AddHlRp(int p) => (byte)(0x09 | (p << 4));
    public static byte Pop(int p) => (byte)(0xC1 | (p << 4));
    public static byte Push(int p) => (byte)(0xC5 | (p << 4));
    public static byte RetCc(int cc) => (byte)(0xC0 | (cc << 3));
    public static byte JpCc(int cc) => (byte)(0xC2 | (cc << 3));
    public static byte CallCc(int cc) => (byte)(0xC4 | (cc << 3));
    public static byte JrCc(int cc) => (byte)(0x20 | (cc << 3));
    public static byte Rst(int n) => (byte)(0xC7 | (n << 3)); // target = n*8

    public static byte CbRotate(int op, int r) => (byte)((op << 3) | r);
    public static byte CbBit(int bit, int r) => (byte)(0x40 | (bit << 3) | r);
    public static byte CbRes(int bit, int r) => (byte)(0x80 | (bit << 3) | r);
    public static byte CbSet(int bit, int r) => (byte)(0xC0 | (bit << 3) | r);

    public static byte EdInC(int r) => (byte)(0x40 | (r << 3));
    public static byte EdOutC(int r) => (byte)(0x41 | (r << 3));
    public static byte EdAdcHlRp(int p) => (byte)(0x4A | (p << 4));
    public static byte EdSbcHlRp(int p) => (byte)(0x42 | (p << 4));
}
