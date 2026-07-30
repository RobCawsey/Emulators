namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>MOVEP — transfers 2 or 4 bytes between a data register and memory at
    /// alternating addresses (EA, EA+2, EA+4, ...), high-order byte first. Built for 8-bit
    /// peripherals wired to one half of the data bus; the Genesis's memory map never actually
    /// exercises this (nothing is wired that way), but real ROMs occasionally still contain
    /// the opcode as compiler noise, so it's worth decoding correctly rather than throwing.
    /// A word transfer only touches Dn's low 16 bits; a long transfer replaces all 32.</summary>
    private int ExecuteMovep(ushort opcode)
    {
        int dataRegister = (opcode >> 9) & 7;
        bool registerToMemory = ((opcode >> 7) & 1) != 0;
        bool isLong = ((opcode >> 6) & 1) != 0;
        int addressRegister = opcode & 7;

        uint address = A[addressRegister] + FetchDisplacement16();
        int byteCount = isLong ? 4 : 2;

        if (registerToMemory)
        {
            uint value = D[dataRegister];
            int shift = (byteCount - 1) * 8;
            for (int i = 0; i < byteCount; i++)
            {
                _bus.WriteByte(address, (byte)(value >> shift));
                address += 2;
                shift -= 8;
            }
        }
        else
        {
            uint value = 0;
            for (int i = 0; i < byteCount; i++)
            {
                value = (value << 8) | _bus.ReadByte(address);
                address += 2;
            }

            if (isLong) D[dataRegister] = value; else WriteDataRegister(dataRegister, Size.Word, value);
        }

        return isLong ? 24 : 16;
    }

    /// <summary>TRAP #n — always raises exception vector 32+n via the standard exception
    /// entry sequence (see <see cref="RaiseException"/>).</summary>
    private int ExecuteTrap(ushort opcode)
    {
        int trapNumber = opcode & 0xF;
        RaiseException(32 + trapNumber);
        return 34;
    }
}
