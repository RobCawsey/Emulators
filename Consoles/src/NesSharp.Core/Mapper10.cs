namespace NesSharp.Core;

/// <summary>
/// MMC4 (mapper 10) — Fire Emblem, Fire Emblem Gaiden (Japan-only FCI/Nintendo games). The
/// same CHR-bank "latch" mechanism as MMC2 (see <see cref="Mapper9"/> for how that works),
/// but with 16KB PRG banking (switchable at $8000-$BFFF, fixed to the last bank at
/// $C000-$FFFF, vs. MMC2's 8KB-granularity three-fixed-banks scheme) and 8KB of always-
/// enabled PRG-RAM at $6000-$7FFF, which MMC2 doesn't have.
/// </summary>
public sealed class Mapper10 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[8 * 1024];

    private byte _prgBank;
    private byte _chrBankFd0;
    private byte _chrBankFe0;
    private byte _chrBankFd1;
    private byte _chrBankFe1;
    private bool _latch0IsFe;
    private bool _latch1IsFe;
    private byte _mirroringBit;

    public Mapper10(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[128 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => (_mirroringBit & 0x01) != 0 ? MirroringMode.Horizontal : MirroringMode.Vertical;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return _prgRam[address - 0x6000];
        }
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
        if (address is >= 0x6000 and < 0x8000)
        {
            _prgRam[address - 0x6000] = value;
            return;
        }

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
