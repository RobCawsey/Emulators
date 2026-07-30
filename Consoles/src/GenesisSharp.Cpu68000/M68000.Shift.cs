namespace GenesisSharp.Cpu68000;

/// <summary>The four shift/rotate flavors, matching the 2-bit type field's encoding order.</summary>
public enum ShiftType
{
    Arithmetic = 0,
    Logical = 1,
    RotateExtend = 2,
    Rotate = 3,
}

public sealed partial class M68000
{
    /// <summary>Runs <paramref name="count"/> single-bit shift/rotate steps and returns the
    /// result plus the flags that come out of it. <c>Extend</c> is null for ROL/ROR, which
    /// don't touch X. A count of zero is the documented no-op case: value unchanged, C and V
    /// cleared, X left alone (signalled the same way, via a null-carry short-circuit below).</summary>
    private (uint Result, bool Carry, bool Overflow, bool? Extend) PerformShift(ShiftType type, bool left, Size size, uint original, int count)
    {
        uint mask = size.Mask();
        uint signBit = size.SignBit();
        uint value = original & mask;

        if (count == 0)
        {
            return (value, false, false, null);
        }

        bool carryOut = false;
        bool overflow = false;
        bool extend = FlagExtend;

        for (int i = 0; i < count; i++)
        {
            if (left)
            {
                bool bitOut = (value & signBit) != 0;
                value = (value << 1) & mask;

                if (type == ShiftType.Rotate && bitOut)
                {
                    value |= 1;
                }
                else if (type == ShiftType.RotateExtend)
                {
                    if (extend) value |= 1;
                    extend = bitOut;
                }

                bool signAfter = (value & signBit) != 0;
                if (type == ShiftType.Arithmetic && bitOut != signAfter)
                {
                    overflow = true;
                }

                carryOut = bitOut;
            }
            else
            {
                bool bitOut = (value & 1) != 0;

                switch (type)
                {
                    case ShiftType.Arithmetic:
                        bool sign = (value & signBit) != 0;
                        value = (value >> 1) | (sign ? signBit : 0);
                        break;
                    case ShiftType.Logical:
                        value >>= 1;
                        break;
                    case ShiftType.Rotate:
                        value >>= 1;
                        if (bitOut) value |= signBit;
                        break;
                    case ShiftType.RotateExtend:
                        value >>= 1;
                        if (extend) value |= signBit;
                        extend = bitOut;
                        break;
                }

                carryOut = bitOut;
            }
        }

        bool? extendResult = type == ShiftType.Rotate ? null : type == ShiftType.RotateExtend ? extend : carryOut;
        return (value & mask, carryOut, overflow, extendResult);
    }

    private void ApplyShiftFlags(Size size, uint result, bool carry, bool overflow, bool? extend)
    {
        uint masked = result & size.Mask();
        FlagNegative = (masked & size.SignBit()) != 0;
        FlagZero = masked == 0;
        FlagOverflow = overflow;
        FlagCarry = carry;
        if (extend.HasValue)
        {
            FlagExtend = extend.Value;
        }
    }

    /// <summary>Register form — group 1110, size field 00/01/10. Timing is exactly 6 (byte/
    /// word) or 8 (long) base cycles plus 2 per shift, per the published timing table.</summary>
    private int ExecuteShiftRegister(ushort opcode)
    {
        int countField = (opcode >> 9) & 7;
        bool left = ((opcode >> 8) & 1) != 0;
        Size size = DecodeImmSize((opcode >> 6) & 3);
        bool countIsRegister = ((opcode >> 5) & 1) != 0;
        var type = (ShiftType)((opcode >> 3) & 3);
        int reg = opcode & 7;

        int count = countIsRegister ? (int)(D[countField] & 0x3F) : countField == 0 ? 8 : countField;

        uint value = D[reg] & size.Mask();
        var (result, carry, overflow, extend) = PerformShift(type, left, size, value, count);
        WriteDataRegister(reg, size, result);
        ApplyShiftFlags(size, result, carry, overflow, extend);

        return (size == Size.Long ? 8 : 6) + 2 * count;
    }

    /// <summary>Memory form — group 1110, size field 11 (word-only, always a single-bit
    /// shift; there is no count field in this encoding).</summary>
    private int ExecuteShiftMemory(ushort opcode)
    {
        var type = (ShiftType)((opcode >> 9) & 3);
        bool left = ((opcode >> 8) & 1) != 0;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);

        uint value = ReadEa(ea, Size.Word);
        var (result, carry, overflow, extend) = PerformShift(type, left, Size.Word, value, count: 1);
        WriteEa(ea, Size.Word, result);
        ApplyShiftFlags(Size.Word, result, carry, overflow, extend);

        return 8 + cycles;
    }

    private int Decode1110(ushort opcode) => ((opcode >> 6) & 3) == 3 ? ExecuteShiftMemory(opcode) : ExecuteShiftRegister(opcode);
}
