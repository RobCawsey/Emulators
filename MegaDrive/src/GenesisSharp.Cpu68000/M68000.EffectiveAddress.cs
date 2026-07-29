namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Decodes a 6-bit (mode, register) effective-address field, consuming any
    /// extension words from the instruction stream (and applying (An)+/-(An) side effects)
    /// along the way. Returns the location plus the extra cycles the address calculation
    /// costs, per the standard 68000 EA timing table.</summary>
    private (EffectiveAddress Ea, int Cycles) DecodeEa(int mode, int reg, Size size)
    {
        switch (mode)
        {
            case 0: return (EffectiveAddress.DataRegister(reg), 0);
            case 1: return (EffectiveAddress.AddressRegister(reg), 0);
            case 2: return (EffectiveAddress.Memory(A[reg]), size == Size.Long ? 8 : 4);
            case 3: return (EffectiveAddress.Memory(PostIncrement(reg, size)), size == Size.Long ? 8 : 4);
            case 4: return (EffectiveAddress.Memory(PreDecrement(reg, size)), size == Size.Long ? 10 : 6);
            case 5: return (EffectiveAddress.Memory(A[reg] + FetchDisplacement16()), size == Size.Long ? 12 : 8);
            case 6: return (EffectiveAddress.Memory(A[reg] + FetchIndexedDisplacement()), size == Size.Long ? 14 : 10);
            case 7:
                return reg switch
                {
                    0 => (EffectiveAddress.Memory(Size.Word.SignExtend(FetchWord())), size == Size.Long ? 12 : 8),
                    1 => (EffectiveAddress.Memory(FetchLong()), size == Size.Long ? 16 : 12),
                    2 => (EffectiveAddress.Memory(PcRelativeDisplacement()), size == Size.Long ? 12 : 8),
                    3 => (EffectiveAddress.Memory(PcRelativeIndexed()), size == Size.Long ? 14 : 10),
                    4 => (EffectiveAddress.Memory(FetchImmediate(size)), size == Size.Long ? 8 : 4),
                    _ => throw new InvalidOperationException($"Reserved effective address mode 7/{reg}."),
                };
            default:
                throw new InvalidOperationException($"Unreachable EA mode {mode}.");
        }
    }

    private uint PostIncrement(int reg, Size size)
    {
        uint address = A[reg];
        A[reg] += (uint)(reg == 7 && size == Size.Byte ? 2 : (int)size);
        return address;
    }

    private uint PreDecrement(int reg, Size size)
    {
        A[reg] -= (uint)(reg == 7 && size == Size.Byte ? 2 : (int)size);
        return A[reg];
    }

    private uint FetchDisplacement16() => unchecked((uint)(short)FetchWord());

    /// <summary>Brief extension word format: D/A(1) Register(3) W/L(1) 00 Displacement(8).</summary>
    private uint FetchIndexedDisplacement() => IndexedDisplacementFrom(baseAddress: 0);

    private uint IndexedDisplacementFrom(uint baseAddress)
    {
        ushort ext = FetchWord();
        bool indexIsAddressRegister = (ext & 0x8000) != 0;
        int indexRegister = (ext >> 12) & 7;
        bool indexIsLong = (ext & 0x0800) != 0;
        sbyte displacement8 = unchecked((sbyte)ext);

        uint indexValue = ReadRegister(indexIsAddressRegister, indexRegister);
        if (!indexIsLong)
        {
            indexValue = Size.Word.SignExtend(indexValue);
        }

        return baseAddress + indexValue + unchecked((uint)(int)displacement8);
    }

    private uint PcRelativeDisplacement()
    {
        uint baseAddress = PC;
        return baseAddress + FetchDisplacement16();
    }

    private uint PcRelativeIndexed()
    {
        uint baseAddress = PC;
        return IndexedDisplacementFrom(baseAddress);
    }

    /// <summary>Returns the address of the meaningful data for an immediate operand, having
    /// already consumed the extension word(s) from the instruction stream. Byte immediates
    /// occupy the low-order byte of a full extension word.</summary>
    private uint FetchImmediate(Size size)
    {
        if (size == Size.Long)
        {
            uint address = PC;
            FetchLong();
            return address;
        }

        uint wordAddress = PC + (size == Size.Byte ? 1u : 0u);
        FetchWord();
        return wordAddress;
    }

    private uint ReadEa(EffectiveAddress ea, Size size) => ea.Kind switch
    {
        EaKind.DataRegister => D[ea.Register] & size.Mask(),
        EaKind.AddressRegister => A[ea.Register] & size.Mask(),
        EaKind.Memory => size switch
        {
            Size.Byte => _bus.ReadByte(ea.Address),
            Size.Word => _bus.ReadWord(ea.Address),
            Size.Long => _bus.ReadLong(ea.Address),
            _ => throw new ArgumentOutOfRangeException(nameof(size)),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(ea)),
    };

    private void WriteEa(EffectiveAddress ea, Size size, uint value)
    {
        switch (ea.Kind)
        {
            case EaKind.DataRegister:
                WriteDataRegister(ea.Register, size, value);
                break;
            case EaKind.AddressRegister:
                WriteAddressRegister(ea.Register, size, value);
                break;
            case EaKind.Memory:
                switch (size)
                {
                    case Size.Byte: _bus.WriteByte(ea.Address, (byte)value); break;
                    case Size.Word: _bus.WriteWord(ea.Address, (ushort)value); break;
                    case Size.Long: _bus.WriteLong(ea.Address, value); break;
                }
                break;
        }
    }
}
