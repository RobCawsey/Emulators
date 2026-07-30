namespace NesSharp.Core;

/// <summary>CNROM (mapper 3) — Gyruss, Spy Hunter, and others. PRG-ROM is fixed (16KB
/// mirrored, or 32KB direct); only CHR banking is switchable, 8KB at a time. No PRG-RAM;
/// mirroring is fixed by the cartridge header.</summary>
public sealed class Mapper3 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private byte _chrBank;

    public MirroringMode Mirroring { get; }

    public Mapper3(Cartridge cartridge)
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
        return _prg[(address - 0x8000) % _prg.Length];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _chrBank = value;
        }
    }

    public byte PpuRead(ushort address)
    {
        int bankCount8k = _chr.Length / 0x2000;
        int bank = _chrBank % bankCount8k;
        return _chr[bank * 0x2000 + address];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            int bankCount8k = _chr.Length / 0x2000;
            int bank = _chrBank % bankCount8k;
            _chr[bank * 0x2000 + address] = value;
        }
    }
}
