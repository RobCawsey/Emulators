namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>MOVE (and its MOVEA form, when the decoded destination is an address
    /// register — same opcode pattern, CCR just isn't touched for that case).</summary>
    private int ExecuteMove(ushort opcode, Size size)
    {
        int srcMode = (opcode >> 3) & 7, srcReg = opcode & 7;
        int dstMode = (opcode >> 6) & 7, dstReg = (opcode >> 9) & 7;

        var (src, srcCycles) = DecodeEa(srcMode, srcReg, size);
        uint value = ReadEa(src, size);

        var (dst, dstCycles) = DecodeEa(dstMode, dstReg, size);
        WriteEa(dst, size, value);

        if (dst.Kind != EaKind.AddressRegister)
        {
            SetFlagsLogic(size, value);
        }

        return 4 + srcCycles + dstCycles;
    }

    private int ExecuteMoveQuick(ushort opcode)
    {
        int register = (opcode >> 9) & 7;
        uint value = unchecked((uint)(int)unchecked((sbyte)opcode));
        D[register] = value;
        SetFlagsLogic(Size.Long, value);
        return 4;
    }

    private int ExecuteLea(ushort opcode)
    {
        int addressRegister = (opcode >> 9) & 7;
        int srcMode = (opcode >> 3) & 7, srcReg = opcode & 7;
        var (ea, cycles) = DecodeEa(srcMode, srcReg, Size.Long);
        if (ea.Kind != EaKind.Memory)
        {
            throw new InvalidOperationException("LEA requires a memory addressing mode.");
        }

        A[addressRegister] = ea.Address;
        return 4 + cycles;
    }

    private int ExecutePea(ushort opcode)
    {
        int srcMode = (opcode >> 3) & 7, srcReg = opcode & 7;
        var (ea, cycles) = DecodeEa(srcMode, srcReg, Size.Long);
        if (ea.Kind != EaKind.Memory)
        {
            throw new InvalidOperationException("PEA requires a memory addressing mode.");
        }

        PushLong(ea.Address);
        return 12 + cycles;
    }
}
