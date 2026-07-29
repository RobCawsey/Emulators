using NesSharp.Core;

namespace NesSharp.Tests;

/// <summary>Minimal IMapper test double — flat 8KB CHR-RAM, no PRG behavior needed for
/// isolated Ppu2C02 tests.</summary>
public sealed class FakeMapper : IMapper
{
    private readonly byte[] _chr = new byte[8 * 1024];

    public MirroringMode Mirroring { get; set; } = MirroringMode.Horizontal;

    public byte CpuRead(ushort address) => 0;
    public void CpuWrite(ushort address, byte value) { }
    public byte PpuRead(ushort address) => _chr[address & 0x1FFF];
    public void PpuWrite(ushort address, byte value) => _chr[address & 0x1FFF] = value;
}
