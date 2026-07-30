using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Hand-assembles the handful of 68000 opcodes the unit tests need, straight from
/// the bit-field formulas rather than hand-computed hex — much easier to check for mistakes.</summary>
internal static class Asm
{
    private static int SizeField(Size size) => size switch
    {
        Size.Byte => 0,
        Size.Word => 1,
        Size.Long => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(size)),
    };

    public static ushort MoveQuick(int reg, int data) => (ushort)(0x7000 | (reg << 9) | (byte)data);

    public static ushort Move(Size size, int srcMode, int srcReg, int dstMode, int dstReg)
    {
        int topNibble = size switch { Size.Byte => 1, Size.Long => 2, Size.Word => 3, _ => throw new ArgumentOutOfRangeException(nameof(size)) };
        return (ushort)((topNibble << 12) | (dstReg << 9) | (dstMode << 6) | (srcMode << 3) | srcReg);
    }

    public static ushort MoveRegToReg(Size size, int srcReg, int dstReg) => Move(size, srcMode: 0, srcReg, dstMode: 0, dstReg);

    public static ushort MoveImmediate(Size size, int dstMode, int dstReg) => Move(size, srcMode: 7, srcReg: 4, dstMode, dstReg);

    public static ushort Lea(int dstAddressRegister, int srcMode, int srcReg) => (ushort)(0x41C0 | (dstAddressRegister << 9) | (srcMode << 3) | srcReg);

    public static ushort AddDnPlusEa(Size size, int dn, int eaMode, int eaReg) => (ushort)(0xD000 | (dn << 9) | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort AddEaPlusDn(Size size, int dn, int eaMode, int eaReg) => (ushort)(0xD000 | (dn << 9) | ((SizeField(size) + 4) << 6) | (eaMode << 3) | eaReg);

    public static ushort SubDnMinusEa(Size size, int dn, int eaMode, int eaReg) => (ushort)(0x9000 | (dn << 9) | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    /// <summary>SUBA.L / ADDA.L — opmode 7 (long). Distinct from SUBX/ADDX despite sharing the
    /// same top nibble and, when the source is Dn/An direct, the same bits5-3 shape — see
    /// M68000.IsSubx/IsAddx's doc comment for why that ambiguity is a real bug this regression
    /// test exists to catch.</summary>
    public static ushort SubaLong(int an, int eaMode, int eaReg) => (ushort)(0x9000 | (an << 9) | (7 << 6) | (eaMode << 3) | eaReg);

    public static ushort AddaLong(int an, int eaMode, int eaReg) => (ushort)(0xD000 | (an << 9) | (7 << 6) | (eaMode << 3) | eaReg);

    public static ushort Cmp(Size size, int dn, int eaMode, int eaReg) => (ushort)(0xB000 | (dn << 9) | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort Eor(Size size, int dn, int eaMode, int eaReg) => (ushort)(0xB000 | (dn << 9) | ((SizeField(size) + 4) << 6) | (eaMode << 3) | eaReg);

    public static ushort AndDnAndEa(Size size, int dn, int eaMode, int eaReg) => (ushort)(0xC000 | (dn << 9) | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort OrEaOrDn(Size size, int dn, int eaMode, int eaReg) => (ushort)(0x8000 | (dn << 9) | ((SizeField(size) + 4) << 6) | (eaMode << 3) | eaReg);

    public static ushort Bcc(Condition condition, sbyte displacement) => (ushort)(0x6000 | ((int)condition << 8) | (byte)displacement);

    public static ushort Bra(sbyte displacement) => (ushort)(0x6000 | (byte)displacement);

    public static ushort Bsr(sbyte displacement) => (ushort)(0x6100 | (byte)displacement);

    public static ushort Jsr(int eaMode, int eaReg) => (ushort)(0x4E80 | (eaMode << 3) | eaReg);

    public static ushort Jmp(int eaMode, int eaReg) => (ushort)(0x4EC0 | (eaMode << 3) | eaReg);

    public const ushort Rts = 0x4E75;
    public const ushort Nop = 0x4E71;

    public static ushort AddQuick(int data, Size size, int eaMode, int eaReg) => (ushort)(0x5000 | ((data == 8 ? 0 : data) << 9) | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort Clr(Size size, int eaMode, int eaReg) => (ushort)(0x4200 | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort Tst(Size size, int eaMode, int eaReg) => (ushort)(0x4A00 | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort ShiftRegister(ShiftType type, bool left, Size size, int count, int reg) =>
        (ushort)(0xE000 | ((count == 8 ? 0 : count) << 9) | ((left ? 1 : 0) << 8) | (SizeField(size) << 6) | ((int)type << 3) | reg);

    public static ushort ShiftRegisterByRegisterCount(ShiftType type, bool left, Size size, int countReg, int reg) =>
        (ushort)(0xE000 | (countReg << 9) | ((left ? 1 : 0) << 8) | (SizeField(size) << 6) | (1 << 5) | ((int)type << 3) | reg);

    public static ushort ShiftMemory(ShiftType type, bool left, int eaMode, int eaReg) =>
        (ushort)(0xE0C0 | ((int)type << 9) | ((left ? 1 : 0) << 8) | (eaMode << 3) | eaReg);

    public static ushort Scc(Condition condition, int eaMode, int eaReg) => (ushort)(0x50C0 | ((int)condition << 8) | (eaMode << 3) | eaReg);

    public static ushort Dbcc(Condition condition, int reg) => (ushort)(0x50C8 | ((int)condition << 8) | reg);

    public static ushort Movem(bool load, Size size, int eaMode, int eaReg) =>
        (ushort)(0x4880 | ((load ? 1 : 0) << 10) | ((size == Size.Long ? 1 : 0) << 6) | (eaMode << 3) | eaReg);

    /// <summary>Register indices are 0-7 = D0-D7, 8-15 = A0-A7. Use with the normal-order
    /// (An)+/other-mode load forms and MOVEM stores to anything other than -(An).</summary>
    public static ushort MaskForLoad(params int[] registerIndices)
    {
        int mask = 0;
        foreach (int r in registerIndices) mask |= 1 << r;
        return (ushort)mask;
    }

    /// <summary>Register indices are 0-7 = D0-D7, 8-15 = A0-A7. Use with the -(An) store
    /// form, whose mask bit order is reversed (bit0 = A7 ... bit15 = D0).</summary>
    public static ushort MaskForStore(params int[] registerIndices)
    {
        int mask = 0;
        foreach (int r in registerIndices) mask |= 1 << (15 - r);
        return (ushort)mask;
    }

    public static ushort ExgDataData(int rx, int ry) => (ushort)(0xC140 | (rx << 9) | ry);

    public static ushort ExgAddressAddress(int rx, int ry) => (ushort)(0xC148 | (rx << 9) | ry);

    public static ushort ExgDataAddress(int rx, int ry) => (ushort)(0xC188 | (rx << 9) | ry);

    public static ushort Tas(int eaMode, int eaReg) => (ushort)(0x4AC0 | (eaMode << 3) | eaReg);

    public static ushort Chk(int reg, int eaMode, int eaReg) => (ushort)(0x4180 | (reg << 9) | (eaMode << 3) | eaReg);

    // opType: 0 = BTST, 1 = BCHG, 2 = BCLR, 3 = BSET.
    public static ushort BitOpStatic(int opType, int eaMode, int eaReg) => (ushort)(0x0800 | (opType << 6) | (eaMode << 3) | eaReg);

    public static ushort BitOpDynamic(int opType, int bitRegister, int eaMode, int eaReg) => (ushort)(0x0100 | (bitRegister << 9) | (opType << 6) | (eaMode << 3) | eaReg);

    public static ushort Addx(Size size, int dx, bool predecrement, int ry) =>
        (ushort)(0xD100 | (dx << 9) | (SizeField(size) << 6) | ((predecrement ? 1 : 0) << 3) | ry);

    public static ushort Subx(Size size, int dx, bool predecrement, int ry) =>
        (ushort)(0x9100 | (dx << 9) | (SizeField(size) << 6) | ((predecrement ? 1 : 0) << 3) | ry);

    public static ushort Abcd(int dx, bool predecrement, int ry) => (ushort)(0xC100 | (dx << 9) | ((predecrement ? 1 : 0) << 3) | ry);

    public static ushort Sbcd(int dx, bool predecrement, int ry) => (ushort)(0x8100 | (dx << 9) | ((predecrement ? 1 : 0) << 3) | ry);

    public static ushort Movep(int dataRegister, bool registerToMemory, bool isLong, int addressRegister) =>
        (ushort)(0x0108 | (dataRegister << 9) | ((registerToMemory ? 1 : 0) << 7) | ((isLong ? 1 : 0) << 6) | addressRegister);

    public static ushort Trap(int number) => (ushort)(0x4E40 | number);

    public const ushort Stop = 0x4E72;
    public const ushort Reset = 0x4E70;
    public const ushort Trapv = 0x4E76;

    public static ushort MoveToCcr(int eaMode, int eaReg) => (ushort)(0x44C0 | (eaMode << 3) | eaReg);

    public static ushort MoveFromSr(int eaMode, int eaReg) => (ushort)(0x40C0 | (eaMode << 3) | eaReg);

    public static ushort MoveToSr(int eaMode, int eaReg) => (ushort)(0x46C0 | (eaMode << 3) | eaReg);

    /// <summary><paramref name="toUsp"/> true = "MOVE An,USP", false = "MOVE USP,An".</summary>
    public static ushort MoveUsp(bool toUsp, int register) => (ushort)(0x4E60 | ((toUsp ? 0 : 1) << 3) | register);

    public static ushort Andi(Size size, int eaMode, int eaReg) => (ushort)(0x0200 | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort Ori(Size size, int eaMode, int eaReg) => (ushort)(0x0000 | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort Neg(Size size, int eaMode, int eaReg) => (ushort)(0x4400 | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    public static ushort Not(Size size, int eaMode, int eaReg) => (ushort)(0x4600 | (SizeField(size) << 6) | (eaMode << 3) | eaReg);

    /// <summary>ADDI with a raw, unchecked size field — used to construct the architecturally
    /// reserved size=3 encoding, which <see cref="Size"/> has no case for (see
    /// M68000.ReservedOpcodeException).</summary>
    public static ushort AddImmediateRawSize(int sizeBits, int eaMode, int eaReg) => (ushort)(0x0600 | (sizeBits << 6) | (eaMode << 3) | eaReg);

    /// <summary>Decode0000's directly-reserved subNibble 0xE — no valid instruction is encoded
    /// under this bit pattern at all, regardless of the remaining bits.</summary>
    public static ushort ReservedSubNibbleE() => 0x0E00;

    public static ushort Mulu(int dn, int eaMode, int eaReg) => (ushort)(0xC0C0 | (dn << 9) | (eaMode << 3) | eaReg);

    public static ushort Muls(int dn, int eaMode, int eaReg) => (ushort)(0xC1C0 | (dn << 9) | (eaMode << 3) | eaReg);

    public static ushort Divu(int dn, int eaMode, int eaReg) => (ushort)(0x80C0 | (dn << 9) | (eaMode << 3) | eaReg);

    public static ushort Divs(int dn, int eaMode, int eaReg) => (ushort)(0x81C0 | (dn << 9) | (eaMode << 3) | eaReg);
}
