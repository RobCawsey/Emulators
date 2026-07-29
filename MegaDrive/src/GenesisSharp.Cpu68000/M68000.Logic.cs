namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>AND (group 1100) / OR (group 1000) — same shape as ADD/SUB, but the
    /// "opmode 3/7" slot is MULU/MULS (AND — see <see cref="ExecuteMultiply"/>) or DIVU/DIVS
    /// (OR — see <see cref="ExecuteDivide"/>).</summary>
    private int ExecuteBitwiseRegisterGroup(ushort opcode, Func<uint, uint, uint> combine, string multiplyOrDivideMnemonic, Func<ushort, int>? multiplyOrDivide = null)
    {
        int reg = (opcode >> 9) & 7;
        int opmode = (opcode >> 6) & 7;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        if (opmode == 3 || opmode == 7)
        {
            if (multiplyOrDivide is not null)
            {
                return multiplyOrDivide(opcode);
            }

            throw new NotImplementedException($"{multiplyOrDivideMnemonic} is not implemented yet.");
        }

        bool eaIsDestination = opmode >= 4;
        Size size = DecodeImmSize(opmode % 4);
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
        uint eaValue = ReadEa(ea, size);
        uint dnValue = D[reg] & size.Mask();
        uint result = combine(eaValue, dnValue);

        if (eaIsDestination)
        {
            WriteEa(ea, size, result);
        }
        else
        {
            WriteDataRegister(reg, size, result);
        }

        SetFlagsLogic(size, result);
        return (size == Size.Long ? 6 : 4) + cycles;
    }

    private int ExecuteAnd(ushort opcode) => ExecuteBitwiseRegisterGroup(opcode, static (a, b) => a & b, "MULU/MULS", ExecuteMultiply);

    private int ExecuteOr(ushort opcode) => ExecuteBitwiseRegisterGroup(opcode, static (a, b) => a | b, "DIVU/DIVS", ExecuteDivide);

    /// <summary>MULU.W / MULS.W — opmode bit 8 distinguishes them (0 = unsigned, 1 = signed);
    /// both multiply a 16-bit &lt;ea&gt; by Dn's low word and store the full 32-bit product back
    /// into Dn. Flags: N/Z from the result, V/C always cleared, X unaffected (<see
    /// cref="SetFlagsLogic"/> already does exactly this).
    ///
    /// Cycle cost follows the documented hardware formula, 38 + 2n, where n is the number of
    /// one-bits in the source operand for MULU, or (per the Motorola manual's MULS rule) the
    /// number of one-bits if the source is non-negative and the number of zero-bits if it's
    /// negative — i.e. a data-dependent cost, not a fixed one like most other instructions in
    /// this core.</summary>
    private int ExecuteMultiply(ushort opcode)
    {
        int reg = (opcode >> 9) & 7;
        bool signed = ((opcode >> 8) & 1) != 0;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);
        ushort source = (ushort)ReadEa(ea, Size.Word);

        uint result;
        int oneBits;
        if (signed)
        {
            short signedSource = unchecked((short)source);
            short signedDn = unchecked((short)(D[reg] & 0xFFFF));
            result = unchecked((uint)((int)signedSource * (int)signedDn));
            oneBits = System.Numerics.BitOperations.PopCount(signedSource >= 0 ? source : (ushort)~source);
        }
        else
        {
            result = source * (D[reg] & 0xFFFFu);
            oneBits = System.Numerics.BitOperations.PopCount(source);
        }

        D[reg] = result;
        SetFlagsLogic(Size.Long, result);
        return 38 + (2 * oneBits) + cycles;
    }

    /// <summary>DIVU.W / DIVS.W — opmode bit 8 distinguishes them (0 = unsigned, 1 = signed);
    /// both divide Dn's full 32 bits by a 16-bit &lt;ea&gt; divisor, leaving a 16-bit quotient in
    /// Dn's low word and a 16-bit remainder in its high word. Divide-by-zero traps via vector 5;
    /// Dn is left untouched, and N/Z/V are all explicitly cleared before the trap is taken (found
    /// via cross-checking against clown68000, itself validated against the SingleStepTests
    /// hardware-test-vector suite — https://github.com/SingleStepTests/m68000).
    ///
    /// A quotient that doesn't fit in 16 bits (signed range for DIVS, unsigned for DIVU) sets V
    /// and leaves Dn unmodified — but N and Z are NOT left alone here despite the manual calling
    /// them "undefined" in this case: real silicon consistently forces N=1 and Z=0, which is
    /// confirmed by that same hardware-test-vector suite. (This was previously implemented as
    /// "leave untouched," reasoned as equally valid for a genuinely-undefined case — that
    /// reasoning was wrong; real hardware doesn't leave them alone, it forces specific values.)
    ///
    /// Otherwise (no overflow): N from the quotient's sign bit (bit 15, even for DIVU's unsigned
    /// result — that's genuinely how the hardware flag works), Z from the quotient being zero,
    /// C always cleared.
    ///
    /// Cycle cost here is a flat approximation, not the precise formula: real DIVU/DIVS timing
    /// depends on the quotient's bit pattern through an iterative restoring-division algorithm,
    /// and unlike MULU/MULS's clean documented popcount rule, I don't have confident recall of
    /// that exact formula. 140 (DIVU) / 158 (DIVS) are the commonly-cited average-case figures;
    /// flagging this as an approximation rather than a verified hardware timing, similar in
    /// spirit to the 6-button pad's timeout constant.</summary>
    private int ExecuteDivide(ushort opcode)
    {
        int reg = (opcode >> 9) & 7;
        bool signed = ((opcode >> 8) & 1) != 0;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);
        ushort divisorRaw = (ushort)ReadEa(ea, Size.Word);

        if (divisorRaw == 0)
        {
            FlagNegative = false;
            FlagZero = false;
            FlagOverflow = false;
            RaiseException(5);
            return 38 + cycles;
        }

        uint dividend = D[reg];
        int quotient;
        int remainder;
        if (signed)
        {
            int divisor = unchecked((short)divisorRaw);
            quotient = (int)dividend / divisor;
            remainder = (int)dividend % divisor;
        }
        else
        {
            quotient = (int)(dividend / divisorRaw);
            remainder = (int)(dividend % divisorRaw);
        }

        bool overflow = signed ? (quotient < short.MinValue || quotient > short.MaxValue) : (uint)quotient > 0xFFFF;
        if (overflow)
        {
            FlagNegative = true;
            FlagZero = false;
            FlagOverflow = true;
            FlagCarry = false;
            return (signed ? 158 : 140) + cycles;
        }

        D[reg] = ((uint)remainder << 16) | ((uint)quotient & 0xFFFF);
        FlagNegative = (quotient & 0x8000) != 0;
        FlagZero = (quotient & 0xFFFF) == 0;
        FlagOverflow = false;
        FlagCarry = false;

        return (signed ? 158 : 140) + cycles;
    }

    /// <summary>ANDI / ORI / EORI. The "to CCR"/"to SR" special form (EA field repurposed as
    /// the immediate-mode marker) is handled by <see cref="ExecuteImmediateLogicToStatusRegister"/>.</summary>
    private int ExecuteImmediateLogic(ushort opcode, Func<uint, uint, uint> combine)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        if (eaMode == 7 && eaReg == 4)
        {
            return ExecuteImmediateLogicToStatusRegister(size, combine);
        }

        uint immediate = ReadEa(EffectiveAddress.Memory(FetchImmediate(size)), size);
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
        uint dst = ReadEa(ea, size);
        uint result = combine(dst, immediate);
        WriteEa(ea, size, result);
        SetFlagsLogic(size, result);

        return (size == Size.Long ? 16 : 8) + cycles;
    }

    private int ExecuteAndImmediate(ushort opcode) => ExecuteImmediateLogic(opcode, static (a, b) => a & b);

    private int ExecuteOrImmediate(ushort opcode) => ExecuteImmediateLogic(opcode, static (a, b) => a | b);

    private int ExecuteEorImmediate(ushort opcode) => ExecuteImmediateLogic(opcode, static (a, b) => a ^ b);

    private int ExecuteNot(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        uint result = ~ReadEa(ea, size);
        WriteEa(ea, size, result);
        SetFlagsLogic(size, result);
        return (size == Size.Long ? 6 : 4) + cycles;
    }

    private int ExecuteClr(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        WriteEa(ea, size, 0);
        SetFlagsLogic(size, 0);
        return (size == Size.Long ? 6 : 4) + cycles;
    }

    private int ExecuteTst(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        SetFlagsLogic(size, ReadEa(ea, size));
        return 4 + cycles;
    }

    /// <summary>TAS — test-and-set. Reads a byte, sets flags exactly like TST would, then
    /// unconditionally forces bit 7 and writes it back. Real hardware does this as a single
    /// locked bus cycle for multi-processor semaphores; a single-core emulator doesn't need
    /// the atomicity, just the read-test-then-write sequence.</summary>
    private int ExecuteTas(ushort opcode)
    {
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Byte);

        uint value = ReadEa(ea, Size.Byte);
        SetFlagsLogic(Size.Byte, value);
        WriteEa(ea, Size.Byte, value | 0x80);

        return (ea.Kind == EaKind.DataRegister ? 4 : 10) + cycles;
    }

    private int ExecuteExtWord(ushort opcode)
    {
        int reg = opcode & 7;
        uint value = Size.Byte.SignExtend(D[reg]) & Size.Word.Mask();
        WriteDataRegister(reg, Size.Word, value);
        SetFlagsLogic(Size.Word, value);
        return 4;
    }

    private int ExecuteExtLong(ushort opcode)
    {
        int reg = opcode & 7;
        uint value = Size.Word.SignExtend(D[reg]);
        D[reg] = value;
        SetFlagsLogic(Size.Long, value);
        return 4;
    }

    private int ExecuteSwap(ushort opcode)
    {
        int reg = opcode & 7;
        uint value = D[reg];
        uint swapped = (value << 16) | (value >> 16);
        D[reg] = swapped;
        SetFlagsLogic(Size.Long, swapped);
        return 4;
    }
}
