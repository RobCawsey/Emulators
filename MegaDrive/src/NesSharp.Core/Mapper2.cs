namespace NesSharp.Core;

/// <summary>UxROM (mapper 2) — Mega Man, Castlevania, Contra, and many others. A single
/// switchable 16KB PRG bank at $8000-$BFFF, fixed to the last bank at $C000-$FFFF. CHR is
/// always RAM (UxROM boards have no CHR-ROM); mirroring is fixed by the cartridge header.
/// </summary>
public sealed class Mapper2 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr = new byte[8 * 1024];
    private byte _prgBank;

    public MirroringMode Mirroring { get; }

    public Mapper2(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        Mirroring = cartridge.Mirroring;
    }

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }
        int bankCount16k = _prg.Length / 0x4000;
        int bank = address < 0xC000 ? _prgBank % bankCount16k : bankCount16k - 1;
        return _prg[bank * 0x4000 + (address & 0x3FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _prgBank = value;
        }
    }

    public byte PpuRead(ushort address) => _chr[address & 0x1FFF];
    public void PpuWrite(ushort address, byte value) => _chr[address & 0x1FFF] = value;
}
