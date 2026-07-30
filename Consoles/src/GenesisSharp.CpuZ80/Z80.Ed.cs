namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    /// <summary>The ED-prefixed subset actually used by real Z80 code: 16-bit ADC/SBC/LD via
    /// (nn), NEG, interrupt-mode control, RETN/RETI, the I/R moves, IN/OUT via (C), the
    /// nibble-rotate pair RLD/RRD, and the full block-transfer/compare/IO family. IX/IY
    /// prefixed forms don't reach here (DD/FD are intercepted before ED would be, and in any
    /// case the Genesis's Z80 sound driver has no reason to combine them).</summary>
    private int ExecuteEdOpcode(byte opcode)
    {
        if ((opcode & 0xC7) == 0x40) return ExecuteInRegisterC((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0x41) return ExecuteOutRegisterC((opcode >> 3) & 7);
        if ((opcode & 0xCF) == 0x4A) return ExecuteAdcHlRp((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0x42) return ExecuteSbcHlRp((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0x4B) return ExecuteLoadRpFromAddress((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0x43) return ExecuteLoadAddressFromRp((opcode >> 4) & 3);

        return opcode switch
        {
            0x44 => ExecuteNeg(),
            0x45 => ExecuteRetn(),
            0x46 => ExecuteIm(0),
            0x47 => ExecuteLdIA(),
            0x4D => ExecuteReti(),
            0x4F => ExecuteLdRA(),
            0x56 => ExecuteIm(1),
            0x57 => ExecuteLdAI(),
            0x5E => ExecuteIm(2),
            0x5F => ExecuteLdAR(),
            0x67 => ExecuteRrd(),
            0x6D => ExecuteRld(),
            0xA0 => ExecuteLdi(),
            0xA1 => ExecuteCpi(),
            0xA2 => ExecuteIni(),
            0xA3 => ExecuteOuti(),
            0xA8 => ExecuteLdd(),
            0xA9 => ExecuteCpd(),
            0xAA => ExecuteInd(),
            0xAB => ExecuteOutd(),
            0xB0 => ExecuteLdir(),
            0xB1 => ExecuteCpir(),
            0xB2 => ExecuteInir(),
            0xB3 => ExecuteOtir(),
            0xB8 => ExecuteLddr(),
            0xB9 => ExecuteCpdr(),
            0xBA => ExecuteIndr(),
            0xBB => ExecuteOtdr(),
            _ => throw Unimplemented(opcode, "ED-prefixed opcode (e.g. duplicate IM forms, undocumented ED NOPs)"),
        };
    }

    private int ExecuteAdcHlRp(int p)
    {
        HL = ApplyAdc16(HL, ReadRp16(p));
        return 15;
    }

    private int ExecuteSbcHlRp(int p)
    {
        HL = ApplySbc16(HL, ReadRp16(p));
        return 15;
    }

    private int ExecuteLoadRpFromAddress(int p)
    {
        ushort address = FetchWord();
        byte low = _bus.ReadByte(address);
        byte high = _bus.ReadByte((ushort)(address + 1));
        WriteRp16(p, (ushort)((high << 8) | low));
        return 20;
    }

    private int ExecuteLoadAddressFromRp(int p)
    {
        ushort address = FetchWord();
        ushort value = ReadRp16(p);
        _bus.WriteByte(address, (byte)value);
        _bus.WriteByte((ushort)(address + 1), (byte)(value >> 8));
        return 20;
    }

    private int ExecuteInRegisterC(int y)
    {
        byte value = _bus.ReadPort(C);
        WriteR8(y, value);
        FlagSign = (value & 0x80) != 0;
        FlagZero = value == 0;
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagParityOverflow = Parity(value);
        SetUndocumentedFlags(value);
        return 12;
    }

    private int ExecuteOutRegisterC(int y)
    {
        _bus.WritePort(C, ReadR8(y));
        return 12;
    }

    private int ExecuteNeg()
    {
        byte original = A;
        SetFlagsSub8(0, original, 0, out byte result);
        A = result;
        return 8;
    }

    private int ExecuteRetn()
    {
        PC = PopWord();
        Iff1 = Iff2;
        return 14;
    }

    private int ExecuteReti()
    {
        PC = PopWord();
        Iff1 = Iff2;
        return 14;
    }

    private int ExecuteIm(int mode)
    {
        InterruptMode = mode;
        return 8;
    }

    private int ExecuteLdIA()
    {
        I = A;
        return 9;
    }

    private int ExecuteLdRA()
    {
        R = A;
        return 9;
    }

    private int ExecuteLdAI() => ExecuteLdAFromSpecial(I);

    private int ExecuteLdAR() => ExecuteLdAFromSpecial(R);

    private int ExecuteLdAFromSpecial(byte value)
    {
        A = value;
        FlagSign = (value & 0x80) != 0;
        FlagZero = value == 0;
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagParityOverflow = Iff2;
        SetUndocumentedFlags(value);
        return 9;
    }

    /// <summary>RLD/RRD treat A's low nibble and (HL)'s two nibbles as a 12-bit value and
    /// rotate it by 4 bits, in opposite directions.</summary>
    private int ExecuteRld()
    {
        byte m = _bus.ReadByte(HL);
        byte newMHi = (byte)(m & 0x0F);
        byte newMLo = (byte)(A & 0x0F);
        byte newALo = (byte)((m >> 4) & 0x0F);
        _bus.WriteByte(HL, (byte)((newMHi << 4) | newMLo));
        A = (byte)((A & 0xF0) | newALo);
        SetNibbleRotateFlags();
        return 18;
    }

    private int ExecuteRrd()
    {
        byte m = _bus.ReadByte(HL);
        byte newMLo = (byte)((m >> 4) & 0x0F);
        byte newMHi = (byte)(A & 0x0F);
        byte newALo = (byte)(m & 0x0F);
        _bus.WriteByte(HL, (byte)((newMHi << 4) | newMLo));
        A = (byte)((A & 0xF0) | newALo);
        SetNibbleRotateFlags();
        return 18;
    }

    private void SetNibbleRotateFlags()
    {
        FlagSign = (A & 0x80) != 0;
        FlagZero = A == 0;
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagParityOverflow = Parity(A);
        SetUndocumentedFlags(A);
    }

    // ---- Block transfer/compare/IO family. Each "Step" does one iteration; the *IR/*DR
    // repeating forms rewind PC by 2 (back onto the ED prefix) to re-execute themselves while
    // their loop condition holds, exactly like real hardware.

    private void LdiStep()
    {
        _bus.WriteByte(DE, _bus.ReadByte(HL));
        HL++; DE++; BC--;
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagParityOverflow = BC != 0;
    }

    private void LddStep()
    {
        _bus.WriteByte(DE, _bus.ReadByte(HL));
        HL--; DE--; BC--;
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagParityOverflow = BC != 0;
    }

    private int ExecuteLdi() { LdiStep(); return 16; }
    private int ExecuteLdd() { LddStep(); return 16; }

    private int ExecuteLdir()
    {
        LdiStep();
        if (BC != 0) { PC -= 2; return 21; }
        return 16;
    }

    private int ExecuteLddr()
    {
        LddStep();
        if (BC != 0) { PC -= 2; return 21; }
        return 16;
    }

    private void CpiStep()
    {
        byte value = _bus.ReadByte(HL);
        SetFlagsSub8(A, value, 0, out _);
        HL++; BC--;
        FlagParityOverflow = BC != 0;
    }

    private void CpdStep()
    {
        byte value = _bus.ReadByte(HL);
        SetFlagsSub8(A, value, 0, out _);
        HL--; BC--;
        FlagParityOverflow = BC != 0;
    }

    private int ExecuteCpi() { CpiStep(); return 16; }
    private int ExecuteCpd() { CpdStep(); return 16; }

    private int ExecuteCpir()
    {
        CpiStep();
        if (BC != 0 && !FlagZero) { PC -= 2; return 21; }
        return 16;
    }

    private int ExecuteCpdr()
    {
        CpdStep();
        if (BC != 0 && !FlagZero) { PC -= 2; return 21; }
        return 16;
    }

    /// <summary>The I/O block instructions' documented flag rules are notoriously intricate
    /// and rarely depended upon; this sets Z/N from the decremented B (the part every
    /// reference agrees on) and leaves the rest as a reasonable approximation.</summary>
    private void IniStep()
    {
        byte value = _bus.ReadPort(C);
        _bus.WriteByte(HL, value);
        HL++; B--;
        FlagZero = B == 0;
        FlagSubtract = true;
    }

    private void IndStep()
    {
        byte value = _bus.ReadPort(C);
        _bus.WriteByte(HL, value);
        HL--; B--;
        FlagZero = B == 0;
        FlagSubtract = true;
    }

    private void OutiStep()
    {
        byte value = _bus.ReadByte(HL);
        _bus.WritePort(C, value);
        HL++; B--;
        FlagZero = B == 0;
        FlagSubtract = true;
    }

    private void OutdStep()
    {
        byte value = _bus.ReadByte(HL);
        _bus.WritePort(C, value);
        HL--; B--;
        FlagZero = B == 0;
        FlagSubtract = true;
    }

    private int ExecuteIni() { IniStep(); return 16; }
    private int ExecuteInd() { IndStep(); return 16; }
    private int ExecuteOuti() { OutiStep(); return 16; }
    private int ExecuteOutd() { OutdStep(); return 16; }

    private int ExecuteInir() { IniStep(); if (B != 0) { PC -= 2; return 21; } return 16; }
    private int ExecuteIndr() { IndStep(); if (B != 0) { PC -= 2; return 21; } return 16; }
    private int ExecuteOtir() { OutiStep(); if (B != 0) { PC -= 2; return 21; } return 16; }
    private int ExecuteOtdr() { OutdStep(); if (B != 0) { PC -= 2; return 21; } return 16; }
}
