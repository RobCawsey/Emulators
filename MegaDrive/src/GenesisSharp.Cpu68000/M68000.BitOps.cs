namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>0 = BTST, 1 = BCHG, 2 = BCLR, 3 = BSET — matches the opcode's own 2-bit
    /// selector field, so callers can pass it straight through.</summary>
    private enum BitOpKind
    {
        Test = 0,
        Change = 1,
        Clear = 2,
        Set = 3,
    }

    private static int BitOpBaseCycles(BitOpKind kind, bool registerDestination)
    {
        if (kind == BitOpKind.Test)
        {
            return registerDestination ? 6 : 4;
        }

        if (kind == BitOpKind.Clear && registerDestination)
        {
            return 10;
        }

        return 8;
    }

    /// <summary>Shared BTST/BCHG/BCLR/BSET core. The bit number wraps mod 32 against a Dn
    /// destination (the whole register is the bit field) or mod 8 against a memory
    /// destination (only the addressed byte is touched) — this is what makes the same
    /// instruction meaningful against both operand kinds.</summary>
    private int ExecuteBitOperation(ushort opcode, uint bitNumber, BitOpKind kind, int extraCycles)
    {
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        Size size = eaMode == 0 ? Size.Long : Size.Byte;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        int bit = (int)(bitNumber % (uint)size.Bits());
        uint mask = 1u << bit;

        uint value = ReadEa(ea, size);
        FlagZero = (value & mask) == 0;

        if (kind != BitOpKind.Test)
        {
            uint result = kind switch
            {
                BitOpKind.Change => value ^ mask,
                BitOpKind.Clear => value & ~mask,
                BitOpKind.Set => value | mask,
                _ => value,
            };
            WriteEa(ea, size, result);
        }

        return BitOpBaseCycles(kind, ea.Kind == EaKind.DataRegister) + cycles + extraCycles;
    }

    /// <summary>Dynamic form — group 0000, bit 8 set: bit number comes from a data register.
    /// EA mode 001 is reserved for MOVEP in this same sub-space, not a bit op.</summary>
    private int ExecuteDynamicBitOperation(ushort opcode)
    {
        int bitNumberRegister = (opcode >> 9) & 7;
        var kind = (BitOpKind)((opcode >> 6) & 3);
        return ExecuteBitOperation(opcode, D[bitNumberRegister], kind, extraCycles: 0);
    }

    /// <summary>Static form — group 0000, sub-nibble 0x8: bit number is an immediate byte in
    /// the extension word that follows the opcode (fetched before the destination EA, since
    /// that's the real instruction-stream order).</summary>
    private int ExecuteStaticBitOperation(ushort opcode)
    {
        var kind = (BitOpKind)((opcode >> 6) & 3);
        uint bitNumber = ReadEa(EffectiveAddress.Memory(FetchImmediate(Size.Byte)), Size.Byte);
        return ExecuteBitOperation(opcode, bitNumber, kind, extraCycles: 4);
    }
}
