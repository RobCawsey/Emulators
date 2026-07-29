namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    /// <summary>The CB-prefixed table is fully regular: bits 7-6 select the operation group
    /// (00=rotate/shift, 01=BIT, 10=RES, 11=SET), bits 5-3 select the shift/rotate type or bit
    /// number, and bits 2-0 select the operand via the same r-index used everywhere else
    /// (0=B..5=L, 6=(HL), 7=A).</summary>
    private int ExecuteCbOpcode(byte opcode)
    {
        int group = (opcode >> 6) & 3;
        int y = (opcode >> 3) & 7;
        int z = opcode & 7;

        switch (group)
        {
            case 0:
                WriteR8(z, ApplyShiftOrRotate8(y, ReadR8(z)));
                return z == 6 ? 15 : 8;
            case 1:
                ApplyBit(y, ReadR8(z));
                return z == 6 ? 12 : 8;
            case 2:
                WriteR8(z, (byte)(ReadR8(z) & ~(1 << y)));
                return z == 6 ? 15 : 8;
            default:
                WriteR8(z, (byte)(ReadR8(z) | (1 << y)));
                return z == 6 ? 15 : 8;
        }
    }

    // Shift/rotate type order (bits 5-3 within group 0): RLC,RRC,RL,RR,SLA,SRA,SLL(undocumented),SRL.
    private byte ApplyShiftOrRotate8(int op, byte value)
    {
        bool carryOut;
        byte result;
        switch (op)
        {
            case 0: // RLC
                carryOut = (value & 0x80) != 0;
                result = (byte)((value << 1) | (carryOut ? 1 : 0));
                break;
            case 1: // RRC
                carryOut = (value & 0x01) != 0;
                result = (byte)((value >> 1) | (carryOut ? 0x80 : 0));
                break;
            case 2: // RL
                carryOut = (value & 0x80) != 0;
                result = (byte)((value << 1) | (FlagCarry ? 1 : 0));
                break;
            case 3: // RR
                carryOut = (value & 0x01) != 0;
                result = (byte)((value >> 1) | (FlagCarry ? 0x80 : 0));
                break;
            case 4: // SLA
                carryOut = (value & 0x80) != 0;
                result = (byte)(value << 1);
                break;
            case 5: // SRA — preserves the sign bit
                carryOut = (value & 0x01) != 0;
                result = (byte)((value >> 1) | (value & 0x80));
                break;
            case 6: // SLL/SL1 — undocumented; shifts left but always fills bit 0 with 1
                carryOut = (value & 0x80) != 0;
                result = (byte)((value << 1) | 1);
                break;
            default: // SRL
                carryOut = (value & 0x01) != 0;
                result = (byte)(value >> 1);
                break;
        }

        FlagCarry = carryOut;
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagSign = (result & 0x80) != 0;
        FlagZero = result == 0;
        FlagParityOverflow = Parity(result);
        SetUndocumentedFlags(result);
        return result;
    }

    /// <summary>BIT b,r — Z/PV mirror "bit is clear"; S is set only for a set bit 7. Unlike
    /// most instructions, the undocumented X/Y flags here are documented to come from the
    /// tested byte for register operands, but from an internal address latch for BIT n,(HL) —
    /// this uses the tested byte in both cases, a common simplification.</summary>
    private void ApplyBit(int bit, byte value)
    {
        bool isSet = (value & (1 << bit)) != 0;
        FlagZero = !isSet;
        FlagParityOverflow = !isSet;
        FlagHalfCarry = true;
        FlagSubtract = false;
        FlagSign = bit == 7 && isSet;
        SetUndocumentedFlags(value);
    }
}
