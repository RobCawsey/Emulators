namespace NesSharp.Core;

/// <summary>
/// MMC1 (mapper 1) — SNROM/SCROM/SKROM/SIROM/SUROM and similar boards. Switchable PRG
/// banking (32KB or 16KB+16KB-fixed), switchable CHR banking (8KB or two independent 4KB
/// banks), a mapper-controlled mirroring mode (the reason <see cref="Ppu2C02"/> now queries
/// <see cref="IMapper.Mirroring"/> live instead of caching it), and 8KB of gate-able PRG-RAM.
///
/// All four internal registers (control, CHR bank 0, CHR bank 1, PRG bank) share one
/// write path: writing anywhere in $8000-$FFFF shifts one bit (from the value's bit 0) into
/// a 5-bit serial shift register; on the 5th write, the accumulated value latches into
/// whichever register the *address of that 5th write* selects. Writing a value with bit 7
/// set resets the shift register and forces PRG mode to 3 (fix last bank at $C000) — real,
/// documented reset behavior, not a simplification.
///
/// Deliberate simplifications: SUROM's extra 512KB-PRG addressing bit isn't implemented
/// (standard MMC1 boards, capped at 256KB PRG / 128KB CHR, cover the large majority of MMC1
/// games); and real hardware ignores a second consecutive write within ~1 CPU cycle of the
/// first (relevant to games using INC/DEC-style read-modify-write on mapper registers) —
/// not modeled here.
/// </summary>
public sealed class Mapper1 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[8 * 1024];

    private byte _shiftRegister;
    private int _shiftCount;

    private byte _control = 0x0C; // power-on: PRG mode 3, CHR mode 0, single-screen-lower
    private byte _chrBank0;
    private byte _chrBank1;
    private byte _prgBank;

    public Mapper1(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => (_control & 0x03) switch
    {
        0 => MirroringMode.SingleScreenLower,
        1 => MirroringMode.SingleScreenUpper,
        2 => MirroringMode.Vertical,
        _ => MirroringMode.Horizontal,
    };

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
        return _prg[ResolvePrgOffset(address)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_prgBank & 0x10) == 0) // PRG-RAM chip enable bit, 0 = enabled
            {
                _prgRam[address - 0x6000] = value;
            }
            return;
        }
        if (address < 0x8000)
        {
            return;
        }

        if ((value & 0x80) != 0)
        {
            _shiftRegister = 0;
            _shiftCount = 0;
            _control |= 0x0C; // force PRG mode 3
            return;
        }

        _shiftRegister |= (byte)((value & 0x01) << _shiftCount);
        _shiftCount++;
        if (_shiftCount < 5)
        {
            return;
        }

        byte loaded = _shiftRegister;
        if (address <= 0x9FFF)
        {
            _control = loaded;
        }
        else if (address <= 0xBFFF)
        {
            _chrBank0 = loaded;
        }
        else if (address <= 0xDFFF)
        {
            _chrBank1 = loaded;
        }
        else
        {
            _prgBank = loaded;
        }
        _shiftRegister = 0;
        _shiftCount = 0;
    }

    public byte PpuRead(ushort address) => _chr[ResolveChrOffset(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            _chr[ResolveChrOffset(address)] = value;
        }
    }

    private int ResolvePrgOffset(ushort address)
    {
        int prgMode = (_control >> 2) & 0x03;
        int bank = _prgBank & 0x0F;
        int bankCount16k = _prg.Length / 0x4000;

        if (prgMode <= 1)
        {
            // 32KB mode: ignore the bank number's low bit, pairing 16KB banks into 32KB units.
            int base32 = (bank & 0xE) * 0x4000;
            return (base32 + (address - 0x8000)) % _prg.Length;
        }
        if (prgMode == 2)
        {
            // Fix the first bank at $8000, switch the bank register's selection at $C000.
            if (address < 0xC000)
            {
                return address - 0x8000;
            }
            return (bank % bankCount16k) * 0x4000 + (address - 0xC000);
        }
        // Mode 3: switch at $8000, fix the last bank at $C000.
        if (address < 0xC000)
        {
            return (bank % bankCount16k) * 0x4000 + (address - 0x8000);
        }
        return (bankCount16k - 1) * 0x4000 + (address - 0xC000);
    }

    private int ResolveChrOffset(ushort address)
    {
        int chrMode = (_control >> 4) & 0x01;
        if (chrMode == 0)
        {
            // 8KB mode: ignore the bank number's low bit, pairing 4KB banks into 8KB units.
            int bank4k = _chrBank0 & 0x1E;
            return (bank4k * 0x1000 + address) % _chr.Length;
        }
        // 4KB mode: two independently-selected banks.
        if (address < 0x1000)
        {
            return (_chrBank0 * 0x1000 + address) % _chr.Length;
        }
        return (_chrBank1 * 0x1000 + (address - 0x1000)) % _chr.Length;
    }
}
