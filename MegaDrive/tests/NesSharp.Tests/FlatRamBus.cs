using NesSharp.Cpu;

namespace NesSharp.Tests;

/// <summary>Flat 64KB RAM/ROM for exercising the CPU core in isolation, with no PPU/APU/
/// mapper involved. Good enough for opcode-level unit tests and Klaus Dormann-style
/// functional test ROMs; the real NES memory map arrives with NesSharp.Core.</summary>
public sealed class FlatRamBus : IBus
{
    private readonly byte[] _ram = new byte[65536];

    public int ReadCount { get; private set; }
    public int WriteCount { get; private set; }

    public byte Read(ushort address)
    {
        ReadCount++;
        return _ram[address];
    }

    public void Write(ushort address, byte value)
    {
        WriteCount++;
        _ram[address] = value;
    }

    public byte Peek(ushort address) => _ram[address];

    public void Load(ushort address, params byte[] bytes) => bytes.CopyTo(_ram, address);

    public void SetResetVector(ushort address)
    {
        _ram[0xFFFC] = (byte)(address & 0xFF);
        _ram[0xFFFD] = (byte)(address >> 8);
    }

    public void SetIrqVector(ushort address)
    {
        _ram[0xFFFE] = (byte)(address & 0xFF);
        _ram[0xFFFF] = (byte)(address >> 8);
    }
}
