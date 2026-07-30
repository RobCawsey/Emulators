namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    // r-index order used throughout the unprefixed and CB tables: 0=B,1=C,2=D,3=E,4=H,5=L,
    // 6=(HL) — or (IX+d)/(IY+d) while an index prefix is active, see Z80.Indexed.cs — 7=A.
    // H and L themselves are NOT substituted for IX/IY's halves; that's the undocumented
    // IXH/IXL/IYH/IYL feature, which this core doesn't implement.
    private byte ReadR8(int index) => index switch
    {
        0 => B, 1 => C, 2 => D, 3 => E, 4 => H, 5 => L,
        6 => _bus.ReadByte(IndexedAddress()),
        7 => A,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    private void WriteR8(int index, byte value)
    {
        switch (index)
        {
            case 0: B = value; break;
            case 1: C = value; break;
            case 2: D = value; break;
            case 3: E = value; break;
            case 4: H = value; break;
            case 5: L = value; break;
            case 6: _bus.WriteByte(IndexedAddress(), value); break;
            case 7: A = value; break;
        }
    }

    // rp-index order for LD rp,nn / INC rp / DEC rp / ADD HL,rp: 0=BC,1=DE,2=HL (IX/IY while
    // indexed),3=SP.
    private ushort ReadRp16(int p) => p switch { 0 => BC, 1 => DE, 2 => IndexBase, 3 => SP, _ => throw new ArgumentOutOfRangeException(nameof(p)) };

    private void WriteRp16(int p, ushort value)
    {
        switch (p)
        {
            case 0: BC = value; break;
            case 1: DE = value; break;
            case 2: SetIndexBase(value); break;
            case 3: SP = value; break;
        }
    }

    // qq-index order for PUSH/POP: 0=BC,1=DE,2=HL (IX/IY while indexed),3=AF (not SP).
    private ushort ReadQq16(int p) => p switch { 0 => BC, 1 => DE, 2 => IndexBase, 3 => AF, _ => throw new ArgumentOutOfRangeException(nameof(p)) };

    private void WriteQq16(int p, ushort value)
    {
        switch (p)
        {
            case 0: BC = value; break;
            case 1: DE = value; break;
            case 2: SetIndexBase(value); break;
            case 3: AF = value; break;
        }
    }

    // Condition-code index order (bits 5-3 of Jcc/CALLcc/RETcc/JR cc opcodes): NZ,Z,NC,C,PO,PE,P,M.
    private bool TestZ80Condition(int cc) => cc switch
    {
        0 => !FlagZero,
        1 => FlagZero,
        2 => !FlagCarry,
        3 => FlagCarry,
        4 => !FlagParityOverflow,
        5 => FlagParityOverflow,
        6 => !FlagSign,
        7 => FlagSign,
        _ => throw new ArgumentOutOfRangeException(nameof(cc)),
    };

    /// <summary>The unprefixed opcode table. The two large regular grids (LD r,r' and ALU
    /// A,r) and several smaller ones (INC/DEC r, LD r,n, LD rp,nn, INC/DEC rp, ADD HL,rp,
    /// PUSH/POP, Jcc/CALLcc/RETcc, RST, ALU A,n) are decoded by mask/shift instead of being
    /// spelled out as 100+ near-duplicate switch arms; everything irregular is an explicit
    /// case in <see cref="ExecuteMainOpcodeExplicit"/>.</summary>
    private int ExecuteMainOpcode(byte opcode)
    {
        if (opcode == 0x76) return ExecuteHalt();
        if (opcode >= 0x40 && opcode <= 0x7F) return ExecuteLoadRegisterToRegister(opcode);
        if (opcode >= 0x80 && opcode <= 0xBF) return ExecuteAluRegisterOp(opcode);

        if ((opcode & 0xC7) == 0x04) return ExecuteIncR((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0x05) return ExecuteDecR((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0x06) return ExecuteLoadRImmediate((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0xC6) return ExecuteAluImmediate((opcode >> 3) & 7);

        if ((opcode & 0xCF) == 0x01) return ExecuteLoadRpImmediate((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0x03) return ExecuteIncRp((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0x09) return ExecuteAddHlRp((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0x0B) return ExecuteDecRp((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0xC1) return ExecutePop((opcode >> 4) & 3);
        if ((opcode & 0xCF) == 0xC5) return ExecutePush((opcode >> 4) & 3);

        if ((opcode & 0xC7) == 0xC0) return ExecuteRetCc((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0xC2) return ExecuteJpCc((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0xC4) return ExecuteCallCc((opcode >> 3) & 7);
        if ((opcode & 0xC7) == 0xC7) return ExecuteRst((byte)(opcode & 0x38));

        return ExecuteMainOpcodeExplicit(opcode);
    }

    private int ExecuteHalt()
    {
        Halted = true;
        return 4;
    }

    private int ExecuteLoadRegisterToRegister(byte opcode)
    {
        int dst = (opcode >> 3) & 7, src = opcode & 7;
        WriteR8(dst, ReadR8(src));
        return dst == 6 || src == 6 ? 7 : 4;
    }

    private int ExecuteIncR(int r)
    {
        WriteR8(r, ApplyInc8(ReadR8(r)));
        return r == 6 ? 11 : 4;
    }

    private int ExecuteDecR(int r)
    {
        WriteR8(r, ApplyDec8(ReadR8(r)));
        return r == 6 ? 11 : 4;
    }

    private int ExecuteLoadRImmediate(int r)
    {
        WriteR8(r, FetchByte());
        return r == 6 ? 10 : 7;
    }

    private int ExecuteLoadRpImmediate(int p)
    {
        WriteRp16(p, FetchWord());
        return 10;
    }

    private int ExecuteIncRp(int p)
    {
        WriteRp16(p, (ushort)(ReadRp16(p) + 1));
        return 6;
    }

    private int ExecuteDecRp(int p)
    {
        WriteRp16(p, (ushort)(ReadRp16(p) - 1));
        return 6;
    }

    private int ExecuteAddHlRp(int p)
    {
        SetIndexBase(ApplyAdd16(IndexBase, ReadRp16(p)));
        return 11;
    }

    private int ExecutePop(int p)
    {
        WriteQq16(p, PopWord());
        return 10;
    }

    private int ExecutePush(int p)
    {
        PushWord(ReadQq16(p));
        return 11;
    }

    private int ExecuteRetCc(int cc)
    {
        if (!TestZ80Condition(cc))
        {
            return 5;
        }

        PC = PopWord();
        return 11;
    }

    private int ExecuteJpCc(int cc)
    {
        ushort target = FetchWord();
        if (TestZ80Condition(cc))
        {
            PC = target;
        }

        return 10;
    }

    private int ExecuteCallCc(int cc)
    {
        ushort target = FetchWord();
        if (!TestZ80Condition(cc))
        {
            return 10;
        }

        PushWord(PC);
        PC = target;
        return 17;
    }

    private int ExecuteRst(byte target)
    {
        PushWord(PC);
        PC = target;
        return 11;
    }
}
