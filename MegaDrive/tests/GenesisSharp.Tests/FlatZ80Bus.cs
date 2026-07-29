using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

/// <summary>Flat 64KB memory plus 256 I/O ports for exercising the Z80 core in isolation —
/// no YM2612/SN76489/bank-window involved. Z80 memory is little-endian.</summary>
public sealed class FlatZ80Bus : IBus
{
    private readonly byte[] _memory = new byte[0x10000];
    private readonly byte[] _ports = new byte[0x100];

    public byte ReadByte(ushort address) => _memory[address];

    public void WriteByte(ushort address, byte value) => _memory[address] = value;

    public byte ReadPort(byte port) => _ports[port];

    public void WritePort(byte port, byte value) => _ports[port] = value;

    public void WriteWord(ushort address, ushort value)
    {
        _memory[address] = (byte)value;
        _memory[(ushort)(address + 1)] = (byte)(value >> 8);
    }

    public void LoadBytes(ushort address, params byte[] bytes) => bytes.CopyTo(_memory, address);
}
