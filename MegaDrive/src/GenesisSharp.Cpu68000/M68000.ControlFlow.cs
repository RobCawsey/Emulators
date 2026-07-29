namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Bcc / BRA / BSR — group 0110. Condition field 0000 = BRA, 0001 = BSR, all
    /// others map directly onto <see cref="Condition"/>. An 8-bit displacement of zero means
    /// the real displacement is a 16-bit extension word.</summary>
    private int ExecuteBranch(ushort opcode)
    {
        uint baseAddress = PC;
        int conditionField = (opcode >> 8) & 0xF;
        sbyte shortDisplacement = unchecked((sbyte)opcode);
        bool wordDisplacement = shortDisplacement == 0;
        uint displacement = wordDisplacement ? FetchDisplacement16() : unchecked((uint)(int)shortDisplacement);
        uint target = baseAddress + displacement;

        if (conditionField == 1)
        {
            PushLong(PC);
            PC = target;
            return 18;
        }

        bool takeBranch = conditionField == 0 || TestCondition((Condition)conditionField);
        if (takeBranch)
        {
            PC = target;
            return 10;
        }

        return wordDisplacement ? 12 : 8;
    }

    private int ExecuteJmp(ushort opcode)
    {
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Long);
        if (ea.Kind != EaKind.Memory)
        {
            throw new InvalidOperationException("JMP requires a memory addressing mode.");
        }

        PC = ea.Address;
        return 8 + cycles;
    }

    private int ExecuteJsr(ushort opcode)
    {
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Long);
        if (ea.Kind != EaKind.Memory)
        {
            throw new InvalidOperationException("JSR requires a memory addressing mode.");
        }

        PushLong(PC);
        PC = ea.Address;
        return 16 + cycles;
    }

    private int ExecuteRts(ushort opcode)
    {
        PC = PopLong();
        return 16;
    }

    /// <summary>RTE — pops SR then PC (the exception stack frame has SR on top, PC below
    /// it). Not privilege-checked yet.</summary>
    private int ExecuteRte(ushort opcode)
    {
        ushort sr = PopWord();
        uint pc = PopLong();
        SR = sr;
        PC = pc;
        return 20;
    }

    /// <summary>RTR — pops CCR (low byte only; the supervisor/interrupt-mask bits are left
    /// alone) then PC.</summary>
    private int ExecuteRtr(ushort opcode)
    {
        ushort ccr = PopWord();
        uint pc = PopLong();
        SR = (ushort)((SR & 0xFF00) | (ccr & 0x00FF));
        PC = pc;
        return 20;
    }

    private int ExecuteNop(ushort opcode) => 4;

    private int ExecuteLink(ushort opcode)
    {
        int reg = opcode & 7;
        short displacement = unchecked((short)FetchWord());
        PushLong(A[reg]);
        A[reg] = A[7];
        A[7] = unchecked((uint)((int)A[7] + displacement));
        return 16;
    }

    private int ExecuteUnlk(ushort opcode)
    {
        int reg = opcode & 7;
        A[7] = A[reg];
        A[reg] = PopLong();
        return 12;
    }
}
