namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    private enum IndexMode { None, Ix, Iy }

    private IndexMode _indexMode = IndexMode.None;
    private sbyte _displacement;

    /// <summary>What r-index 6 ((HL) in the unprefixed table) and rp/qq-index 2 (HL as a
    /// 16-bit register) resolve to for the instruction currently decoding. This is the whole
    /// trick that lets DD/FD reuse the existing main-table dispatcher instead of a duplicate
    /// 256-entry table: everywhere the main table would touch HL, it transparently touches
    /// IX/IY instead while a prefixed instruction is in flight.</summary>
    private ushort IndexBase
    {
        get => _indexMode switch { IndexMode.Ix => IX, IndexMode.Iy => IY, _ => HL };
    }

    private void SetIndexBase(ushort value)
    {
        switch (_indexMode)
        {
            case IndexMode.Ix: IX = value; break;
            case IndexMode.Iy: IY = value; break;
            default: HL = value; break;
        }
    }

    /// <summary>The effective address r-index 6 reads/writes: (HL) when no prefix is active,
    /// or (IX+d)/(IY+d) — using the displacement byte already fetched for this instruction —
    /// when one is.</summary>
    private ushort IndexedAddress() => _indexMode == IndexMode.None ? HL : (ushort)(IndexBase + _displacement);

    /// <summary>True for exactly the opcodes whose unprefixed form touches (HL) as a memory
    /// operand (LD r,(HL) / LD (HL),r / ALU A,(HL) / INC (HL) / DEC (HL) / LD (HL),n) — these
    /// are the only ones that need a displacement byte fetched before <see
    /// cref="ExecuteMainOpcode"/> runs. Every other opcode either doesn't touch HL at all, or
    /// touches it purely as a 16-bit register (LD HL,nn, PUSH HL, ADD HL,rr, ...), which needs
    /// no displacement since IX/IY themselves aren't addressed indirectly.</summary>
    private static bool OpcodeUsesIndexedMemory(byte opcode)
    {
        if (opcode == 0x76) return false; // HALT — not a memory reference
        if (opcode >= 0x40 && opcode <= 0x7F) return ((opcode >> 3) & 7) == 6 || (opcode & 7) == 6;
        if (opcode >= 0x80 && opcode <= 0xBF) return (opcode & 7) == 6;
        if ((opcode & 0xC7) == 0x04) return ((opcode >> 3) & 7) == 6; // INC (HL)
        if ((opcode & 0xC7) == 0x05) return ((opcode >> 3) & 7) == 6; // DEC (HL)
        if ((opcode & 0xC7) == 0x06) return ((opcode >> 3) & 7) == 6; // LD (HL),n
        return false;
    }

    /// <summary>Entry point for both DD and FD. Handles prefix chains (the last one before a
    /// non-prefix byte wins), the documented "DD/FD immediately followed by ED" case (the
    /// index prefix is simply discarded), the DDCB/FDCB indexed bit-op table, and the general
    /// case of routing through the existing unprefixed dispatcher with HL substituted.
    /// Timing is approximate: +4 for instructions that don't touch indexed memory (matching
    /// the wasted prefix fetch), +12 for the ones that do (matching the extra address
    /// computation) — these match the well-known constants for the common cases but haven't
    /// been cross-checked instruction-by-instruction against silicon.</summary>
    private int ExecuteIndexedPrefix(IndexMode mode)
    {
        byte next = FetchByte();

        switch (next)
        {
            case 0xDD: return 4 + ExecuteIndexedPrefix(IndexMode.Ix);
            case 0xFD: return 4 + ExecuteIndexedPrefix(IndexMode.Iy);
            case 0xED: return 4 + ExecuteEdOpcode(FetchByte()); // the index prefix is discarded
        }

        _indexMode = mode;
        try
        {
            if (next == 0xCB)
            {
                // ExecuteIndexedCbOpcode returns the standard total (23/20), which already
                // accounts for the DD/FD and CB prefix bytes — don't add another 4 here.
                _displacement = unchecked((sbyte)FetchByte());
                return ExecuteIndexedCbOpcode(FetchByte());
            }

            bool usesIndexedMemory = OpcodeUsesIndexedMemory(next);
            if (usesIndexedMemory)
            {
                _displacement = unchecked((sbyte)FetchByte());
            }

            return ExecuteMainOpcode(next) + (usesIndexedMemory ? 12 : 4);
        }
        finally
        {
            _indexMode = IndexMode.None;
        }
    }

    /// <summary>DDCB/FDCB — always operates on (IX+d)/(IY+d) regardless of the low 3 bits of
    /// the CB-style opcode byte. Real hardware also copies the result into the named register
    /// when those bits aren't 6 (an undocumented quirk); no assembler emits that form, so this
    /// doesn't reproduce it.</summary>
    private int ExecuteIndexedCbOpcode(byte cbOpcode)
    {
        int group = (cbOpcode >> 6) & 3;
        int y = (cbOpcode >> 3) & 7;
        ushort address = IndexedAddress();
        byte value = _bus.ReadByte(address);

        switch (group)
        {
            case 0:
                _bus.WriteByte(address, ApplyShiftOrRotate8(y, value));
                return 23;
            case 1:
                ApplyBit(y, value);
                return 20;
            case 2:
                _bus.WriteByte(address, (byte)(value & ~(1 << y)));
                return 23;
            default:
                _bus.WriteByte(address, (byte)(value | (1 << y)));
                return 23;
        }
    }
}
