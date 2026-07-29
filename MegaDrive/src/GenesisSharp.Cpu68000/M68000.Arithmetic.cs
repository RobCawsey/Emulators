namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Size field 3 is architecturally reserved across every instruction family that
    /// uses this 2-bit encoding — see <see cref="ReservedOpcodeException"/> for why that's a
    /// real 68000 illegal-instruction trap, not a raw crash.</summary>
    private static Size DecodeImmSize(int bits) => bits switch
    {
        0 => Size.Byte,
        1 => Size.Word,
        2 => Size.Long,
        _ => throw new ReservedOpcodeException(),
    };

    /// <summary>ADD / ADDA — group 1101, opmode selects size/direction (see spec table).</summary>
    private int ExecuteAdd(ushort opcode)
    {
        int reg = (opcode >> 9) & 7;
        int opmode = (opcode >> 6) & 7;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        if (opmode == 3 || opmode == 7)
        {
            Size size = opmode == 3 ? Size.Word : Size.Long;
            var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
            uint operand = size.SignExtend(ReadEa(ea, size));
            A[reg] += operand;
            return (size == Size.Word ? 8 : 6) + cycles;
        }

        bool eaIsDestination = opmode >= 4;
        Size opSize = DecodeImmSize(opmode % 4);
        var (dataEa, dataCycles) = DecodeEa(eaMode, eaReg, opSize);
        uint eaValue = ReadEa(dataEa, opSize);
        uint dnValue = D[reg] & opSize.Mask();

        if (!eaIsDestination)
        {
            uint result = dnValue + eaValue;
            WriteDataRegister(reg, opSize, result);
            SetFlagsAdd(opSize, eaValue, dnValue, result);
        }
        else
        {
            uint result = eaValue + dnValue;
            WriteEa(dataEa, opSize, result);
            SetFlagsAdd(opSize, dnValue, eaValue, result);
        }

        return (opSize == Size.Long ? 6 : 4) + dataCycles;
    }

    private int ExecuteAddImmediate(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        uint immediate = ReadEa(EffectiveAddress.Memory(FetchImmediate(size)), size);
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
        uint dst = ReadEa(ea, size);
        uint result = dst + immediate;
        WriteEa(ea, size, result);
        SetFlagsAdd(size, immediate, dst, result);

        return (size == Size.Long ? 16 : 8) + cycles;
    }

    private int ExecuteAddQuick(ushort opcode)
    {
        uint data = (uint)(((opcode >> 9) & 7) is 0 ? 8 : (opcode >> 9) & 7);
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        if (ea.Kind == EaKind.AddressRegister)
        {
            A[ea.Register] += data;
            return 8 + cycles;
        }

        uint dst = ReadEa(ea, size);
        uint result = dst + data;
        WriteEa(ea, size, result);
        SetFlagsAdd(size, data, dst, result);
        return (size == Size.Long ? 8 : 4) + cycles;
    }

    /// <summary>SUB / SUBA — group 1001, mirrors <see cref="ExecuteAdd"/>.</summary>
    private int ExecuteSub(ushort opcode)
    {
        int reg = (opcode >> 9) & 7;
        int opmode = (opcode >> 6) & 7;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        if (opmode == 3 || opmode == 7)
        {
            Size size = opmode == 3 ? Size.Word : Size.Long;
            var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
            uint operand = size.SignExtend(ReadEa(ea, size));
            A[reg] -= operand;
            return (size == Size.Word ? 8 : 6) + cycles;
        }

        bool eaIsDestination = opmode >= 4;
        Size opSize = DecodeImmSize(opmode % 4);
        var (dataEa, dataCycles) = DecodeEa(eaMode, eaReg, opSize);
        uint eaValue = ReadEa(dataEa, opSize);
        uint dnValue = D[reg] & opSize.Mask();

        if (!eaIsDestination)
        {
            uint result = dnValue - eaValue;
            WriteDataRegister(reg, opSize, result);
            SetFlagsSub(opSize, eaValue, dnValue, result);
        }
        else
        {
            uint result = eaValue - dnValue;
            WriteEa(dataEa, opSize, result);
            SetFlagsSub(opSize, dnValue, eaValue, result);
        }

        return (opSize == Size.Long ? 6 : 4) + dataCycles;
    }

    private int ExecuteSubImmediate(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        uint immediate = ReadEa(EffectiveAddress.Memory(FetchImmediate(size)), size);
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
        uint dst = ReadEa(ea, size);
        uint result = dst - immediate;
        WriteEa(ea, size, result);
        SetFlagsSub(size, immediate, dst, result);

        return (size == Size.Long ? 16 : 8) + cycles;
    }

    private int ExecuteSubQuick(ushort opcode)
    {
        uint data = (uint)(((opcode >> 9) & 7) is 0 ? 8 : (opcode >> 9) & 7);
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        if (ea.Kind == EaKind.AddressRegister)
        {
            A[ea.Register] -= data;
            return 8 + cycles;
        }

        uint dst = ReadEa(ea, size);
        uint result = dst - data;
        WriteEa(ea, size, result);
        SetFlagsSub(size, data, dst, result);
        return (size == Size.Long ? 8 : 4) + cycles;
    }

    private int ExecuteNeg(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);

        uint original = ReadEa(ea, size);
        uint result = 0u - original;
        WriteEa(ea, size, result);
        SetFlagsSub(size, original, 0, result);

        return (size == Size.Long ? 6 : 4) + cycles;
    }
}
