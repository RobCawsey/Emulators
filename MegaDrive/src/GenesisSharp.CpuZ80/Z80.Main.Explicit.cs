namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    /// <summary>Everything in the unprefixed table that doesn't fit one of the regular grids
    /// handled in <see cref="ExecuteMainOpcode"/>.</summary>
    private int ExecuteMainOpcodeExplicit(byte opcode) => opcode switch
    {
        0x00 => 4, // NOP
        0x02 => WriteByteReturn((ushort)BC, A, 7), // LD (BC),A
        0x07 => ExecuteRlca(),
        0x08 => ExecuteExAfAf(),
        0x0A => ReadByteIntoA(BC, 7), // LD A,(BC)
        0x0F => ExecuteRrca(),
        0x10 => ExecuteDjnz(),
        0x12 => WriteByteReturn(DE, A, 7), // LD (DE),A
        0x17 => ExecuteRla(),
        0x18 => ExecuteJr(),
        0x1A => ReadByteIntoA(DE, 7), // LD A,(DE)
        0x1F => ExecuteRra(),
        0x20 => ExecuteJrCc(0),
        0x22 => ExecuteLoadAddressFromHl(),
        0x27 => ExecuteDaa(),
        0x28 => ExecuteJrCc(1),
        0x2A => ExecuteLoadHlFromAddress(),
        0x2F => ExecuteCpl(),
        0x30 => ExecuteJrCc(2),
        0x32 => WriteByteReturn(FetchWord(), A, 13), // LD (nn),A
        0x37 => ExecuteScf(),
        0x38 => ExecuteJrCc(3),
        0x3A => ReadByteIntoA(FetchWord(), 13), // LD A,(nn)
        0x3F => ExecuteCcf(),

        0xC3 => ExecuteJp(),
        0xC9 => ExecuteRet(),
        0xCD => ExecuteCall(),
        0xD3 => ExecuteOutImmediate(),
        0xD9 => ExecuteExx(),
        0xDB => ExecuteInImmediate(),
        0xE3 => ExecuteExSpHl(),
        0xE9 => ExecuteJpHl(),
        0xEB => ExecuteExDeHl(),
        0xF3 => ExecuteDi(),
        0xF9 => ExecuteLoadSpHl(),
        0xFB => ExecuteEi(),

        _ => throw Unimplemented(opcode, "unassigned opcode"),
    };

    private int WriteByteReturn(ushort address, byte value, int cycles)
    {
        _bus.WriteByte(address, value);
        return cycles;
    }

    private int ReadByteIntoA(ushort address, int cycles)
    {
        A = _bus.ReadByte(address);
        return cycles;
    }

    private int ExecuteRlca()
    {
        bool carryOut = (A & 0x80) != 0;
        A = (byte)((A << 1) | (carryOut ? 1 : 0));
        FlagCarry = carryOut;
        FlagHalfCarry = false;
        FlagSubtract = false;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteRrca()
    {
        bool carryOut = (A & 0x01) != 0;
        A = (byte)((A >> 1) | (carryOut ? 0x80 : 0));
        FlagCarry = carryOut;
        FlagHalfCarry = false;
        FlagSubtract = false;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteRla()
    {
        bool carryIn = FlagCarry;
        bool carryOut = (A & 0x80) != 0;
        A = (byte)((A << 1) | (carryIn ? 1 : 0));
        FlagCarry = carryOut;
        FlagHalfCarry = false;
        FlagSubtract = false;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteRra()
    {
        bool carryIn = FlagCarry;
        bool carryOut = (A & 0x01) != 0;
        A = (byte)((A >> 1) | (carryIn ? 0x80 : 0));
        FlagCarry = carryOut;
        FlagHalfCarry = false;
        FlagSubtract = false;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteExAfAf()
    {
        (A, AltA) = (AltA, A);
        (F, AltF) = (AltF, F);
        return 4;
    }

    private int ExecuteExx()
    {
        (B, AltB) = (AltB, B);
        (C, AltC) = (AltC, C);
        (D, AltD) = (AltD, D);
        (E, AltE) = (AltE, E);
        (H, AltH) = (AltH, H);
        (L, AltL) = (AltL, L);
        return 4;
    }

    private int ExecuteDjnz()
    {
        sbyte displacement = unchecked((sbyte)FetchByte());
        B--;
        if (B != 0)
        {
            PC = (ushort)(PC + displacement);
            return 13;
        }

        return 8;
    }

    private int ExecuteJr()
    {
        sbyte displacement = unchecked((sbyte)FetchByte());
        PC = (ushort)(PC + displacement);
        return 12;
    }

    private int ExecuteJrCc(int cc)
    {
        sbyte displacement = unchecked((sbyte)FetchByte());
        if (TestZ80Condition(cc))
        {
            PC = (ushort)(PC + displacement);
            return 12;
        }

        return 7;
    }

    private int ExecuteLoadAddressFromHl()
    {
        ushort address = FetchWord();
        ushort value = IndexBase;
        _bus.WriteByte(address, (byte)value);
        _bus.WriteByte((ushort)(address + 1), (byte)(value >> 8));
        return 16;
    }

    private int ExecuteLoadHlFromAddress()
    {
        ushort address = FetchWord();
        byte low = _bus.ReadByte(address);
        byte high = _bus.ReadByte((ushort)(address + 1));
        SetIndexBase((ushort)((high << 8) | low));
        return 16;
    }

    private int ExecuteDaa()
    {
        byte a = A;
        int correction = 0;
        bool carry = FlagCarry;
        bool halfCarry = FlagHalfCarry;

        if (halfCarry || (a & 0x0F) > 9)
        {
            correction += 0x06;
        }

        if (carry || a > 0x99)
        {
            correction += 0x60;
            carry = true;
        }

        if (FlagSubtract)
        {
            halfCarry = halfCarry && (a & 0x0F) < 6;
            a = (byte)(a - correction);
        }
        else
        {
            halfCarry = (a & 0x0F) > 9;
            a = (byte)(a + correction);
        }

        A = a;
        FlagCarry = carry;
        FlagHalfCarry = halfCarry;
        FlagSign = (a & 0x80) != 0;
        FlagZero = a == 0;
        FlagParityOverflow = Parity(a);
        SetUndocumentedFlags(a);
        return 4;
    }

    private int ExecuteCpl()
    {
        A = (byte)~A;
        FlagHalfCarry = true;
        FlagSubtract = true;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteScf()
    {
        FlagCarry = true;
        FlagHalfCarry = false;
        FlagSubtract = false;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteCcf()
    {
        FlagHalfCarry = FlagCarry;
        FlagCarry = !FlagCarry;
        FlagSubtract = false;
        SetUndocumentedFlags(A);
        return 4;
    }

    private int ExecuteJp()
    {
        PC = FetchWord();
        return 10;
    }

    private int ExecuteRet()
    {
        PC = PopWord();
        return 10;
    }

    private int ExecuteCall()
    {
        ushort target = FetchWord();
        PushWord(PC);
        PC = target;
        return 17;
    }

    private int ExecuteOutImmediate()
    {
        byte port = FetchByte();
        _bus.WritePort(port, A);
        return 11;
    }

    private int ExecuteInImmediate()
    {
        byte port = FetchByte();
        A = _bus.ReadPort(port);
        return 11;
    }

    private int ExecuteExSpHl()
    {
        ushort temp = IndexBase;
        byte low = _bus.ReadByte(SP);
        byte high = _bus.ReadByte((ushort)(SP + 1));
        SetIndexBase((ushort)((high << 8) | low));
        _bus.WriteByte(SP, (byte)temp);
        _bus.WriteByte((ushort)(SP + 1), (byte)(temp >> 8));
        return 19;
    }

    private int ExecuteJpHl()
    {
        PC = IndexBase;
        return 4;
    }

    private int ExecuteExDeHl()
    {
        ushort temp = DE;
        DE = IndexBase;
        SetIndexBase(temp);
        return 4;
    }

    private int ExecuteDi()
    {
        Iff1 = false;
        Iff2 = false;
        return 4;
    }

    private int ExecuteEi()
    {
        Iff1 = true;
        Iff2 = true;
        return 4;
    }

    private int ExecuteLoadSpHl()
    {
        SP = IndexBase;
        return 6;
    }
}
