namespace NesSharp.Core;

/// <summary>
/// MMC2 (mapper 9) — used by exactly one commercial game, Punch-Out!!, but a famous one for
/// its CHR-bank "latch" trick: each 4KB half of the pattern table ($0000-$0FFF and
/// $1000-$1FFF) has two candidate banks, and the PPU's own pattern-table fetches pick which
/// one is active by reading a specific sentinel tile — tile $FD or $FE — before switching
/// to the region that needs it. Real games place matching content at tile $FD/$FE in both
/// candidate banks specifically so this dual-purpose read works.
///
/// The latch update is a side effect of <see cref="PpuRead"/> itself (the same call the PPU
/// makes for every pattern-table fetch already), triggered by the *second half* of a tile's
/// bytes specifically ($xFD8-$xFDF / $xFE8-$xFEF, i.e. the high bitplane) — a real hardware
/// detail, not an approximation.
/// </summary>
public sealed class Mapper9 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;

    private byte _prgBank;
    private byte _chrBankFd0;
    private byte _chrBankFe0;
    private byte _chrBankFd1;
    private byte _chrBankFe1;
    private bool _latch0IsFe;
    private bool _latch1IsFe;
    private byte _mirroringBit;

    public Mapper9(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[128 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => (_mirroringBit & 0x01) != 0 ? MirroringMode.Horizontal : MirroringMode.Vertical;

    public byte CpuRead(ushort address)
    {
        if (address < 0x8000)
        {
            return 0;
        }
        int bankCount8k = _prg.Length / 0x2000;
        int bank = address switch
        {
            < 0xA000 => _prgBank % bankCount8k,
            < 0xC000 => bankCount8k - 3,
            < 0xE000 => bankCount8k - 2,
            _ => bankCount8k - 1,
        };
        return _prg[bank * 0x2000 + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        switch (address)
        {
            case >= 0xA000 and < 0xB000: _prgBank = (byte)(value & 0x0F); break;
            case >= 0xB000 and < 0xC000: _chrBankFd0 = (byte)(value & 0x1F); break;
            case >= 0xC000 and < 0xD000: _chrBankFe0 = (byte)(value & 0x1F); break;
            case >= 0xD000 and < 0xE000: _chrBankFd1 = (byte)(value & 0x1F); break;
            case >= 0xE000 and < 0xF000: _chrBankFe1 = (byte)(value & 0x1F); break;
            case >= 0xF000: _mirroringBit = (byte)(value & 0x01); break;
        }
    }

    public byte PpuRead(ushort address)
    {
        UpdateLatches(address);
        int bankCount4k = _chr.Length / 0x1000;
        if (address < 0x1000)
        {
            int bank = (_latch0IsFe ? _chrBankFe0 : _chrBankFd0) % bankCount4k;
            return _chr[bank * 0x1000 + address];
        }
        int bank1 = (_latch1IsFe ? _chrBankFe1 : _chrBankFd1) % bankCount4k;
        return _chr[bank1 * 0x1000 + (address - 0x1000)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (!_chrIsRam)
        {
            return;
        }
        UpdateLatches(address);
        int bankCount4k = _chr.Length / 0x1000;
        if (address < 0x1000)
        {
            int bank = (_latch0IsFe ? _chrBankFe0 : _chrBankFd0) % bankCount4k;
            _chr[bank * 0x1000 + address] = value;
        }
        else
        {
            int bank = (_latch1IsFe ? _chrBankFe1 : _chrBankFd1) % bankCount4k;
            _chr[bank * 0x1000 + (address - 0x1000)] = value;
        }
    }

    private void UpdateLatches(ushort address)
    {
        switch (address)
        {
            case >= 0x0FD8 and <= 0x0FDF: _latch0IsFe = false; break;
            case >= 0x0FE8 and <= 0x0FEF: _latch0IsFe = true; break;
            case >= 0x1FD8 and <= 0x1FDF: _latch1IsFe = false; break;
            case >= 0x1FE8 and <= 0x1FEF: _latch1IsFe = true; break;
        }
    }
}
