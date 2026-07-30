namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    // ADDX/SUBX (in the ADD/SUB groups) and ABCD/SBCD (in the AND/OR groups) all occupy the
    // same nested bit pattern: opmode's "EA is a data register" slot (4 for byte-only BCD, 4-6
    // for the sized X instructions) combined with EA mode restricted to Dn-direct or -(An) via
    // bits 5-4 fixed at 00 and bit 3 selecting which. Real hardware repurposes exactly that
    // corner instead of leaving it as a redundant "OP Dn,Dn"/"OP Dn,-(An)" encoding.
    //
    // That 0xF130 mask alone is ambiguous, though: it leaves bits 7-6 (ADDX/SUBX's size field,
    // 00/01/10) completely unchecked, so it also matches ADDA.L/SUBA.L (opmode 111, bits 7-6=11)
    // whenever their <ea> source happens to be Dn or An direct — e.g. SUBA.L A0,A1, which is
    // exactly the pointer-subtraction GCC/SGDK emits right after a strlen-style null-terminator
    // scan. Found via real-ROM testing: that ordinary instruction was misrouted into SUBX,
    // which rejected it as reserved and fired the illegal-instruction trap on completely valid
    // code. The extra bits-7-6-aren't-11 check excludes exactly that overlap.
    private static bool IsAddx(ushort opcode) => (opcode & 0xF130) == 0xD100 && (opcode & 0x00C0) != 0x00C0;
    private static bool IsSubx(ushort opcode) => (opcode & 0xF130) == 0x9100 && (opcode & 0x00C0) != 0x00C0;
    private static bool IsAbcd(ushort opcode) => (opcode & 0xF1F0) == 0xC100;
    private static bool IsSbcd(ushort opcode) => (opcode & 0xF1F0) == 0x8100;

    /// <summary>ADDX/SUBX/ABCD/SBCD share a documented Z-flag quirk: the flag is cleared when
    /// the result is nonzero, but never forced back to 1 when the result is zero. That lets a
    /// chain of these (building up a wider add/subtract one byte/word/long at a time) leave Z
    /// meaningfully reflecting "the whole multi-precision result was zero" — an intermediate
    /// zero limb doesn't erase a nonzero result seen in an earlier chunk.</summary>
    private void ClearZeroIfNonzero(uint result)
    {
        if (result != 0)
        {
            FlagZero = false;
        }
    }

    private int ExecuteAddx(ushort opcode)
    {
        int dx = (opcode >> 9) & 7;
        Size size = DecodeImmSize((opcode >> 6) & 3);
        bool predecrement = ((opcode >> 3) & 1) != 0;
        int ry = opcode & 7;

        uint src, dst;
        EffectiveAddress? memDst = null;

        if (!predecrement)
        {
            src = D[ry] & size.Mask();
            dst = D[dx] & size.Mask();
        }
        else
        {
            uint srcAddr = PreDecrement(ry, size);
            uint dstAddr = PreDecrement(dx, size);
            src = ReadEa(EffectiveAddress.Memory(srcAddr), size);
            dst = ReadEa(EffectiveAddress.Memory(dstAddr), size);
            memDst = EffectiveAddress.Memory(dstAddr);
        }

        ulong wideSum = (ulong)(dst & size.Mask()) + (src & size.Mask()) + (FlagExtend ? 1u : 0u);
        uint result = (uint)wideSum & size.Mask();

        if (memDst.HasValue) WriteEa(memDst.Value, size, result); else WriteDataRegister(dx, size, result);

        uint sign = size.SignBit();
        FlagNegative = (result & sign) != 0;
        ClearZeroIfNonzero(result);
        FlagOverflow = (((~dst ^ src) & (dst ^ result)) & sign) != 0;
        FlagCarry = wideSum > size.Mask();
        FlagExtend = FlagCarry;

        return predecrement ? (size == Size.Long ? 30 : 18) : (size == Size.Long ? 8 : 4);
    }

    private int ExecuteSubx(ushort opcode)
    {
        int dx = (opcode >> 9) & 7;
        Size size = DecodeImmSize((opcode >> 6) & 3);
        bool predecrement = ((opcode >> 3) & 1) != 0;
        int ry = opcode & 7;

        uint src, dst;
        EffectiveAddress? memDst = null;

        if (!predecrement)
        {
            src = D[ry] & size.Mask();
            dst = D[dx] & size.Mask();
        }
        else
        {
            uint srcAddr = PreDecrement(ry, size);
            uint dstAddr = PreDecrement(dx, size);
            src = ReadEa(EffectiveAddress.Memory(srcAddr), size);
            dst = ReadEa(EffectiveAddress.Memory(dstAddr), size);
            memDst = EffectiveAddress.Memory(dstAddr);
        }

        long wideDiff = (long)(dst & size.Mask()) - (src & size.Mask()) - (FlagExtend ? 1 : 0);
        uint result = (uint)wideDiff & size.Mask();

        if (memDst.HasValue) WriteEa(memDst.Value, size, result); else WriteDataRegister(dx, size, result);

        uint sign = size.SignBit();
        FlagNegative = (result & sign) != 0;
        ClearZeroIfNonzero(result);
        FlagOverflow = (((dst ^ src) & (dst ^ result)) & sign) != 0;
        FlagCarry = wideDiff < 0;
        FlagExtend = FlagCarry;

        return predecrement ? (size == Size.Long ? 30 : 18) : (size == Size.Long ? 8 : 4);
    }

    /// <summary>Decimal-adjust-after-addition on a single packed BCD byte (two 0-9 digits).</summary>
    private static (byte Result, bool Carry) BcdAdd(byte dst, byte src, bool carryIn)
    {
        int c = carryIn ? 1 : 0;
        int sum = dst + src + c;
        if (((dst & 0xF) + (src & 0xF) + c) > 9)
        {
            sum += 6;
        }

        bool carry = sum > 0x99;
        if (carry)
        {
            sum += 0x60;
        }

        return ((byte)sum, carry);
    }

    /// <summary>Decimal-adjust-after-subtraction, mirroring <see cref="BcdAdd"/>.</summary>
    private static (byte Result, bool Borrow) BcdSub(byte dst, byte src, bool borrowIn)
    {
        int b = borrowIn ? 1 : 0;
        int diff = dst - src - b;
        if (((dst & 0xF) - (src & 0xF) - b) < 0)
        {
            diff -= 6;
        }

        bool borrow = diff < 0;
        if (borrow)
        {
            diff -= 0x60;
        }

        return ((byte)diff, borrow);
    }

    private (byte Src, byte Dst, EffectiveAddress? MemoryDestination) ReadBcdOperands(int dx, int ry, bool predecrement)
    {
        if (!predecrement)
        {
            return ((byte)D[ry], (byte)D[dx], null);
        }

        uint srcAddr = PreDecrement(ry, Size.Byte);
        uint dstAddr = PreDecrement(dx, Size.Byte);
        byte src = (byte)ReadEa(EffectiveAddress.Memory(srcAddr), Size.Byte);
        byte dst = (byte)ReadEa(EffectiveAddress.Memory(dstAddr), Size.Byte);
        return (src, dst, EffectiveAddress.Memory(dstAddr));
    }

    private void WriteBcdResult(int dx, EffectiveAddress? memoryDestination, byte result)
    {
        if (memoryDestination.HasValue) WriteEa(memoryDestination.Value, Size.Byte, result); else WriteDataRegister(dx, Size.Byte, result);
    }

    /// <summary>ABCD/SBCD leave V officially undefined and this emulator leaves it untouched
    /// rather than guessing; N is likewise not meaningfully defined for decimal but is set
    /// from the result's top bit here, matching common practice.</summary>
    private int ExecuteAbcd(ushort opcode)
    {
        int dx = (opcode >> 9) & 7;
        bool predecrement = ((opcode >> 3) & 1) != 0;
        int ry = opcode & 7;

        var (src, dst, memDst) = ReadBcdOperands(dx, ry, predecrement);
        var (result, carry) = BcdAdd(dst, src, FlagExtend);
        WriteBcdResult(dx, memDst, result);

        FlagCarry = carry;
        FlagExtend = carry;
        ClearZeroIfNonzero(result);
        FlagNegative = (result & 0x80) != 0;

        return predecrement ? 18 : 6;
    }

    private int ExecuteSbcd(ushort opcode)
    {
        int dx = (opcode >> 9) & 7;
        bool predecrement = ((opcode >> 3) & 1) != 0;
        int ry = opcode & 7;

        var (src, dst, memDst) = ReadBcdOperands(dx, ry, predecrement);
        var (result, borrow) = BcdSub(dst, src, FlagExtend);
        WriteBcdResult(dx, memDst, result);

        FlagCarry = borrow;
        FlagExtend = borrow;
        ClearZeroIfNonzero(result);
        FlagNegative = (result & 0x80) != 0;

        return predecrement ? 18 : 6;
    }
}
