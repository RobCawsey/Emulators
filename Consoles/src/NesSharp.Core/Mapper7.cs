namespace NesSharp.Core;

/// <summary>AxROM (mapper 7) — Battletoads, Rocket Ranger. A single switchable 32KB PRG
/// bank covering all of $8000-$FFFF; CHR is always RAM; and the mapper directly controls
/// single-screen mirroring (which physical nametable page is used), rather than the
/// cartridge header supplying a fixed horizontal/vertical mode.</summary>
public sealed class Mapper7 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr = new byte[8 * 1024];
    private byte _control;

    public Mapper7(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
    }

    public MirroringMode Mirroring =>
        (_control & 0x10) != 0 ? MirroringMode.SingleScreenUpper : MirroringMode.SingleScreenLower;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }
        int bankCount32k = _prg.Length / 0x8000;
        int bank = (_control & 0x07) % bankCount32k;
        return _prg[bank * 0x8000 + (address - 0x8000)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address >= 0x8000)
        {
            _control = value;
        }
    }

    public byte PpuRead(ushort address) => _chr[address & 0x1FFF];
    public void PpuWrite(ushort address, byte value) => _chr[address & 0x1FFF] = value;
}
