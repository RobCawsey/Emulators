namespace NesSharp.Core;

/// <summary>NROM (mapper 0). PRG-ROM is either 16KB (mirrored twice to fill $8000-$FFFF)
/// or 32KB (mapped directly); no PRG-RAM, and PRG-ROM is fixed — writes are ignored.</summary>
public sealed class Mapper0 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;

    public MirroringMode Mirroring { get; }

    public Mapper0(Cartridge cartridge)
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
            return 0; // no PRG-RAM on plain NROM
        }
        return _prg[(address - 0x8000) % _prg.Length];
    }

    public void CpuWrite(ushort address, byte value)
    {
        // PRG-ROM; nothing to write to.
    }

    public byte PpuRead(ushort address) => _chr[address & 0x1FFF];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            _chr[address & 0x1FFF] = value;
        }
        // CHR-ROM ignores writes.
    }
}
