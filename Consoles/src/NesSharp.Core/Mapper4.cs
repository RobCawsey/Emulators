namespace NesSharp.Core;

/// <summary>
/// MMC3 (mapper 4) — TxROM and similar boards (Super Mario Bros. 3, Kirby's Adventure, Mega
/// Man 3-6, many others). Eight internal registers (R0-R7) reached through a bank-select/
/// bank-data register pair at $8000/$8001, independently switchable PRG (8KB granularity)
/// and CHR (1KB/2KB granularity) banking, a mirroring control bit, gate-able PRG-RAM, and
/// MMC3's signature feature: a scanline counter that clocks off the PPU's A12 address line
/// (bit 12) rather than off CPU cycles, letting games retrigger effects (status bar splits,
/// etc.) at a specific scanline via IRQ.
///
/// A12 clocking is wired through <see cref="Ppu2C02"/> calling <see cref="NotifyA12"/> on
/// every PPU bus access (nametable fetches toggle bit 12 too, exactly as the real shared
/// PPU address bus would present it — this isn't restricted to pattern-table addresses).
/// This implementation clocks the IRQ counter on every 0-to-1 transition with no low-time
/// filter — real hardware requires A12 to have been low for a short window first, to reject
/// glitches from rapid re-fetching. That filter turned out not to be needed here: this passes
/// all four of blargg's mmc3_irq_tests ROMs (1.Clocking, 2.Details, 3.A12_clocking,
/// 4.Scanline_timing — see Mmc3IrqBlarggTests) as-is.
///
/// Deliberate simplifications: four-screen (extra internal RAM) MMC3 boards aren't
/// implemented — the $A000 mirroring register always applies; MMC6-specific extra PRG-RAM
/// protection nuances aren't modeled.
/// </summary>
public sealed class Mapper4 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[8 * 1024];

    private byte _bankSelect;
    private readonly byte[] _chrBanks = new byte[6]; // R0-R5
    private readonly byte[] _prgBanks = new byte[2]; // R6-R7
    private byte _mirroringBit;
    private byte _ramControl;

    private byte _irqLatch;
    private byte _irqCounter;
    private bool _irqReloadPending;
    private bool _irqEnabled;
    private bool _irqPending;
    private bool _previousA12;

    public Mapper4(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => (_mirroringBit & 0x01) != 0 ? MirroringMode.Horizontal : MirroringMode.Vertical;

    public bool IrqLine => _irqPending;

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            return (_ramControl & 0x80) != 0 ? _prgRam[address - 0x6000] : (byte)0;
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
            if ((_ramControl & 0x80) != 0 && (_ramControl & 0x40) == 0)
            {
                _prgRam[address - 0x6000] = value;
            }
            return;
        }
        if (address < 0x8000)
        {
            return;
        }

        switch (address & 0xE001)
        {
            case 0x8000:
                _bankSelect = value;
                break;
            case 0x8001:
            {
                int target = _bankSelect & 0x07;
                if (target <= 5)
                {
                    _chrBanks[target] = value;
                }
                else
                {
                    _prgBanks[target - 6] = (byte)(value & 0x3F);
                }
                break;
            }
            case 0xA000:
                _mirroringBit = (byte)(value & 0x01);
                break;
            case 0xA001:
                _ramControl = value;
                break;
            case 0xC000:
                _irqLatch = value;
                break;
            case 0xC001:
                _irqReloadPending = true;
                break;
            case 0xE000:
                _irqEnabled = false;
                _irqPending = false;
                break;
            case 0xE001:
                _irqEnabled = true;
                break;
        }
    }

    public byte PpuRead(ushort address) => _chr[ResolveChrOffset(address)];

    public void PpuWrite(ushort address, byte value)
    {
        if (_chrIsRam)
        {
            _chr[ResolveChrOffset(address)] = value;
        }
    }

    public void NotifyA12(ushort ppuAddress)
    {
        bool a12 = (ppuAddress & 0x1000) != 0;
        if (!_previousA12 && a12)
        {
            ClockIrqCounter();
        }
        _previousA12 = a12;
    }

    private void ClockIrqCounter()
    {
        if (_irqCounter == 0 || _irqReloadPending)
        {
            _irqCounter = _irqLatch;
            _irqReloadPending = false;
        }
        else
        {
            _irqCounter--;
        }
        if (_irqCounter == 0 && _irqEnabled)
        {
            _irqPending = true;
        }
    }

    private int ResolvePrgOffset(ushort address)
    {
        int bankCount8k = _prg.Length / 0x2000;
        int r6 = _prgBanks[0] % bankCount8k;
        int r7 = _prgBanks[1] % bankCount8k;
        int secondLast = (bankCount8k - 2 + bankCount8k) % bankCount8k;
        int last = bankCount8k - 1;

        bool prgMode1 = (_bankSelect & 0x40) != 0;
        int slot = (address - 0x8000) / 0x2000;
        int bank = slot switch
        {
            0 => prgMode1 ? secondLast : r6,
            1 => r7,
            2 => prgMode1 ? r6 : secondLast,
            _ => last,
        };
        return bank * 0x2000 + (address & 0x1FFF);
    }

    private int ResolveChrOffset(ushort address)
    {
        int slot = address / 0x400;
        int offset = address % 0x400;
        int bankCount1k = _chr.Length / 0x400;
        int bank = GetChrBankForSlot(slot) % bankCount1k;
        return bank * 0x400 + offset;
    }

    private int GetChrBankForSlot(int slot)
    {
        bool inverted = (_bankSelect & 0x80) != 0;
        if (inverted)
        {
            slot ^= 0x04; // swapping the two halves is equivalent to XOR-ing the slot's bit2
        }
        return slot switch
        {
            0 => _chrBanks[0] & 0xFE,
            1 => (_chrBanks[0] & 0xFE) + 1,
            2 => _chrBanks[1] & 0xFE,
            3 => (_chrBanks[1] & 0xFE) + 1,
            4 => _chrBanks[2],
            5 => _chrBanks[3],
            6 => _chrBanks[4],
            _ => _chrBanks[5],
        };
    }
}
