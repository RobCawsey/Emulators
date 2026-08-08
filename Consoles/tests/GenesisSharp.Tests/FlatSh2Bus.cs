using GenesisSharp.CpuSh2;

namespace GenesisSharp.Tests;

/// <summary>Flat 1MB big-endian memory for exercising the SH-2 core in isolation — no 32X
/// memory-map/CS0-CS3 decode involved. Good enough for opcode-level unit tests; the real 32X
/// memory map arrives with 32X bus integration. Addresses are masked into the flat block (unlike
/// FlatMemoryBus, which doesn't need to since 68000 tests naturally stay within its 24-bit
/// range) — SH-2 test code often has real reasons to compute addresses well outside a 1MB window
/// (VBR relocation tests, high stack-pointer values), and masking avoids that being a spurious
/// test-harness crash rather than a deliberate test of wraparound behavior.</summary>
public sealed class FlatSh2Bus : IBus
{
    private const uint Mask = 0xF_FFFF; // 1MB, matching FlatMemoryBus's size

    private readonly byte[] _memory = new byte[0x10_0000];

    public byte ReadByte(uint address) => _memory[address & Mask];

    public ushort ReadWord(uint address)
    {
        uint a = address & Mask;
        return (ushort)((_memory[a] << 8) | _memory[(a + 1) & Mask]);
    }

    public uint ReadLong(uint address) => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

    public void WriteByte(uint address, byte value) => _memory[address & Mask] = value;

    public void WriteWord(uint address, ushort value)
    {
        uint a = address & Mask;
        _memory[a] = (byte)(value >> 8);
        _memory[(a + 1) & Mask] = (byte)value;
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }

    public void LoadWords(uint address, params ushort[] words)
    {
        foreach (ushort word in words)
        {
            WriteWord(address, word);
            address += 2;
        }
    }
}
