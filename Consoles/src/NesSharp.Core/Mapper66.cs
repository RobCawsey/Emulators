namespace NesSharp.Core;

/// <summary>GxROM/MxROM (mapper 66) — Super Mario Bros. + Duck Hunt, Dragon Power. A single
/// register selects both a 32KB PRG bank and an 8KB CHR bank. Mirroring is fixed by the
/// cartridge header.</summary>
public sealed class Mapper66 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private byte _control;

    public MirroringMode Mirroring { get; }

    public Mapper66(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
        Mirroring = cartridge.Mirroring;
    }

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }
        int bankCount32k = _prg.Length / 0x8000;
        int bank = ((_control >> 4) & 0x03) % bankCount32k;
        return _prg[bank * 0x8000 + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _control = value;
        }
    }

    public byte PpuRead(ushort address)
    {
        int bankCount8k = _chr.Length / 0x2000;
        int bank = (_control & 0x03) % bankCount8k;
        return _chr[bank * 0x2000 + address];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            int bankCount8k = _chr.Length / 0x2000;
            int bank = (_control & 0x03) % bankCount8k;
            _chr[bank * 0x2000 + address] = value;
        }
    }
}
