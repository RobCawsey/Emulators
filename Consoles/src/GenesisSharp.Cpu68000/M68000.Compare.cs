namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Group 1011: CMP / CMPA / EOR share this opcode group, distinguished by
    /// opmode exactly like the ADD/SUB groups. (CMPM — the postincrement-postincrement
    /// compare — is not decoded separately yet and falls through to the EOR path, which is
    /// wrong for that one rare encoding; tracked as a known gap.)</summary>
    private int ExecuteCmpOrEor(ushort opcode)
    {
        int reg = (opcode >> 9) & 7;
        int opmode = (opcode >> 6) & 7;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        if (opmode == 3 || opmode == 7)
        {
            Size size = opmode == 3 ? Size.Word : Size.Long;
            var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
            uint operand = size.SignExtend(ReadEa(ea, size));
            uint result = A[reg] - operand;
            SetFlagsCompare(Size.Long, operand, A[reg], result);
            return 6 + cycles;
        }

        if (opmode <= 2)
        {
            Size size = DecodeImmSize(opmode);
            var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
            uint eaValue = ReadEa(ea, size);
            uint dnValue = D[reg] & size.Mask();
            uint result = dnValue - eaValue;
            SetFlagsCompare(size, eaValue, dnValue, result);
            return (size == Size.Long ? 6 : 4) + cycles;
        }

        Size eorSize = DecodeImmSize(opmode - 4);
        var (dst, dstCycles) = DecodeEa(eaMode, eaReg, eorSize);
        uint eaVal = ReadEa(dst, eorSize);
        uint dn = D[reg] & eorSize.Mask();
        uint eorResult = eaVal ^ dn;
        WriteEa(dst, eorSize, eorResult);
        SetFlagsLogic(eorSize, eorResult);
        return (eorSize == Size.Long ? 8 : 4) + dstCycles;
    }

    private int ExecuteCmpImmediate(ushort opcode)
    {
        Size size = DecodeImmSize((opcode >> 6) & 3);
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;

        uint immediate = ReadEa(EffectiveAddress.Memory(FetchImmediate(size)), size);
        var (ea, cycles) = DecodeEa(eaMode, eaReg, size);
        uint dst = ReadEa(ea, size);
        uint result = dst - immediate;
        SetFlagsCompare(size, immediate, dst, result);

        return (size == Size.Long ? 14 : 8) + cycles;
    }
}
