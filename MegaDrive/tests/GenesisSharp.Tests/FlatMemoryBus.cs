using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Flat 1MB big-endian memory for exercising the 68000 core in isolation — no VDP/
/// Z80/memory-map involved. Good enough for opcode-level unit tests; the real Genesis memory
/// map arrives with GenesisSharp.Core.</summary>
public sealed class FlatMemoryBus : IBus
{
    private readonly byte[] _memory = new byte[0x10_0000];

    public byte ReadByte(uint address) => _memory[address];

    public ushort ReadWord(uint address) => (ushort)((_memory[address] << 8) | _memory[address + 1]);

    public uint ReadLong(uint address) => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

    public void WriteByte(uint address, byte value) => _memory[address] = value;

    public void WriteWord(uint address, ushort value)
    {
        _memory[address] = (byte)(value >> 8);
        _memory[address + 1] = (byte)value;
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
