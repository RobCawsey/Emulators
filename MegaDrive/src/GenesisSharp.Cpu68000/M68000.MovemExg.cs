namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    private uint ReadFullRegister(int index) => index < 8 ? D[index] : A[index - 8];

    private void WriteFullRegister(int index, uint value)
    {
        if (index < 8) D[index] = value; else A[index - 8] = value;
    }

    private static int MovemBaseCycles(Size size, int registerCount) => 4 + registerCount * (size == Size.Long ? 8 : 4);

    /// <summary>MOVEM — register list load/store. The register-mask bit order is reversed
    /// for the -(An) store form only (bit0 = A7 ... bit15 = D0, matching the descending
    /// address order registers are written in); every other addressing mode uses the normal
    /// bit0 = D0 ... bit15 = A7 order. Word-sized loads always sign-extend into the full
    /// 32-bit register; word-sized stores just write the register's low 16 bits.</summary>
    private int ExecuteMovem(ushort opcode)
    {
        bool load = ((opcode >> 10) & 1) != 0;
        Size size = ((opcode >> 6) & 1) != 0 ? Size.Long : Size.Word;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        ushort registerMask = FetchWord();
        int registerCount = System.Numerics.BitOperations.PopCount(registerMask);

        if (!load && eaMode == 4) // -(An): store only, predecrement, reversed bit order
        {
            uint address = A[eaReg];
            for (int bit = 0; bit < 16; bit++)
            {
                if ((registerMask & (1 << bit)) == 0) continue;
                int registerIndex = 15 - bit; // bit0 -> A7 (index 15) ... bit15 -> D0 (index 0)
                address -= (uint)size;
                WriteEa(EffectiveAddress.Memory(address), size, ReadFullRegister(registerIndex));
            }

            A[eaReg] = address;
            return MovemBaseCycles(size, registerCount);
        }

        if (load && eaMode == 3) // (An)+: load only, postincrement, normal bit order
        {
            uint address = A[eaReg];
            for (int bit = 0; bit < 16; bit++)
            {
                if ((registerMask & (1 << bit)) == 0) continue;
                uint value = ReadEa(EffectiveAddress.Memory(address), size);
                WriteFullRegister(bit, size.SignExtend(value));
                address += (uint)size;
            }

            A[eaReg] = address;
            return MovemBaseCycles(size, registerCount);
        }

        var (ea, eaCycles) = DecodeEa(eaMode, eaReg, size);
        if (ea.Kind != EaKind.Memory)
        {
            throw new InvalidOperationException("MOVEM requires a memory addressing mode.");
        }

        uint runningAddress = ea.Address;
        for (int bit = 0; bit < 16; bit++)
        {
            if ((registerMask & (1 << bit)) == 0) continue;

            if (load)
            {
                uint value = ReadEa(EffectiveAddress.Memory(runningAddress), size);
                WriteFullRegister(bit, size.SignExtend(value));
            }
            else
            {
                WriteEa(EffectiveAddress.Memory(runningAddress), size, ReadFullRegister(bit));
            }

            runningAddress += (uint)size;
        }

        return MovemBaseCycles(size, registerCount) + eaCycles;
    }

    /// <summary>EXG — swaps two registers outright. Occupies a bit pattern nested inside what
    /// would otherwise be the AND register-op encoding (opmode bits 8-6 = 101, EA mode = Dn
    /// direct); this dispatch must run before <see cref="ExecuteAnd"/> gets a look.</summary>
    private int ExecuteExg(ushort opcode)
    {
        int rx = (opcode >> 9) & 7;
        int ry = opcode & 7;
        int mode = (opcode >> 3) & 0x1F;

        if (mode == 0b01000) // Dx,Dy
        {
            (D[rx], D[ry]) = (D[ry], D[rx]);
        }
        else if (mode == 0b01001) // Ax,Ay
        {
            (A[rx], A[ry]) = (A[ry], A[rx]);
        }
        else // Dx,Ay
        {
            (D[rx], A[ry]) = (A[ry], D[rx]);
        }

        return 6;
    }

    private static bool IsExg(ushort opcode) =>
        (opcode & 0xF1F8) is 0xC140 or 0xC148 or 0xC188;
}
