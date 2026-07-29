namespace NesSharp.Core;

/// <summary>
/// VRC6 (Konami) — Akumajou Densetsu (the Japanese original of Castlevania III, whose US
/// release used a different mapper), Madara, Esper Dream 2. 16KB PRG bank at $8000-$BFFF,
/// 8KB PRG bank at $C000-$DFFF, fixed last 8KB at $E000-$FFFF; eight independently
/// switchable 1KB CHR banks; mapper-controlled mirroring; a CPU-cycle-driven IRQ counter
/// (unlike MMC3's PPU-A12-driven one); and VRC6's signature expansion audio — two pulse
/// channels with a 16-step duty cycle (finer than the 2A03's 4 steps, and no sweep unit)
/// plus a unique sawtooth channel.
///
/// Two physical variants exist — VRC6a (mapper 24) and VRC6b (mapper 26) — differing only
/// in which CPU address lines are wired to the mapper's internal register-select logic (a
/// PCB routing difference, not a behavioral one). Only VRC6a's address decode is
/// implemented; VRC6b is registered under the same class as a documented simplification,
/// since its only two games (Esper Dream 2, Madara) are obscure Japan-exclusive titles.
///
/// The IRQ counter's exact behavior (count up from a latch, firing on overflow past $FF;
/// "scanline mode" prescaling CPU cycles by 3 to approximate one clock per PPU scanline) is
/// implemented from the documented VRC-series design shared with VRC4/VRC7, at reasonably
/// high confidence, but — unlike MMC3's IRQ, which was checked against real hardware capture
/// via blargg's test ROMs — has no equivalent independent test ROM available to verify
/// against here.
/// </summary>
public sealed class Mapper24 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[8 * 1024];

    private byte _prgBank16k;
    private byte _prgBank8k;
    private readonly byte[] _chrBanks = new byte[8];
    private byte _mirroringAndRamControl;

    private readonly Vrc6PulseChannel _pulse1 = new();
    private readonly Vrc6PulseChannel _pulse2 = new();
    private readonly Vrc6SawtoothChannel _sawtooth = new();

    private byte _irqLatch;
    private bool _irqEnabled;
    private bool _irqCycleMode;
    private bool _irqPending;
    private byte _irqCounter;
    private int _irqPrescaler = 341;

    public Mapper24(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => ((_mirroringAndRamControl >> 2) & 0x03) switch
    {
        0 => MirroringMode.Vertical,
        1 => MirroringMode.Horizontal,
        2 => MirroringMode.SingleScreenLower,
        _ => MirroringMode.SingleScreenUpper,
    };

    public bool IrqLine => _irqPending;

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
        if (address < 0xC000)
        {
            int bank = _prgBank16k % bankCount16k;
            return _prg[bank * 0x4000 + (address & 0x3FFF)];
        }
        int bankCount8k = _prg.Length / 0x2000;
        if (address < 0xE000)
        {
            int bank = _prgBank8k % bankCount8k;
            return _prg[bank * 0x2000 + (address & 0x1FFF)];
        }
        return _prg[(bankCount8k - 1) * 0x2000 + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            _prgRam[address - 0x6000] = value;
            return;
        }
        if (address < 0x8000)
        {
            return;
        }

        switch (address & 0xF000)
        {
            case 0x8000:
                _prgBank16k = value;
                break;
            case 0x9000:
                WritePulseRegister(_pulse1, address & 0x03, value);
                break;
            case 0xA000:
                WritePulseRegister(_pulse2, address & 0x03, value);
                break;
            case 0xB000:
                if ((address & 0x03) == 3)
                {
                    _mirroringAndRamControl = value;
                }
                else
                {
                    WriteSawtoothRegister(address & 0x03, value);
                }
                break;
            case 0xC000:
                _prgBank8k = value;
                break;
            case 0xD000:
                _chrBanks[address & 0x03] = value;
                break;
            case 0xE000:
                _chrBanks[4 + (address & 0x03)] = value;
                break;
            case 0xF000:
                WriteIrqRegister(address & 0x03, value);
                break;
        }
    }

    private static void WritePulseRegister(Vrc6PulseChannel pulse, int sub, byte value)
    {
        switch (sub)
        {
            case 0: pulse.WriteControl(value); break;
            case 1: pulse.WriteFrequencyLow(value); break;
            case 2: pulse.WriteFrequencyHigh(value); break;
        }
    }

    private void WriteSawtoothRegister(int sub, byte value)
    {
        switch (sub)
        {
            case 0: _sawtooth.WriteControl(value); break;
            case 1: _sawtooth.WriteFrequencyLow(value); break;
            case 2: _sawtooth.WriteFrequencyHigh(value); break;
        }
    }

    private void WriteIrqRegister(int sub, byte value)
    {
        switch (sub)
        {
            case 0:
                _irqLatch = value;
                break;
            case 1:
                _irqEnabled = (value & 0x02) != 0;
                _irqCycleMode = (value & 0x04) != 0;
                if (_irqEnabled)
                {
                    _irqCounter = _irqLatch;
                    _irqPrescaler = 341;
                }
                _irqPending = false;
                break;
            case 2:
                _irqPending = false;
                break;
        }
    }

    public void ClockCpu()
    {
        _pulse1.ClockTimer();
        _pulse2.ClockTimer();
        _sawtooth.ClockTimer();

        if (!_irqEnabled)
        {
            return;
        }
        if (_irqCycleMode)
        {
            ClockIrqCounter();
        }
        else
        {
            _irqPrescaler -= 3;
            if (_irqPrescaler <= 0)
            {
                _irqPrescaler += 341;
                ClockIrqCounter();
            }
        }
    }

    private void ClockIrqCounter()
    {
        if (_irqCounter == 0xFF)
        {
            _irqCounter = _irqLatch;
            _irqPending = true;
        }
        else
        {
            _irqCounter++;
        }
    }

    /// <summary>Normalizes the three channels (pulses 0-15 each, sawtooth 0-31) to roughly
    /// the same 0..~1 scale as the 2A03 mixer's own output, then applies a modest overall
    /// weight so expansion audio doesn't overpower the built-in channels — real per-board
    /// analog mixing ratios aren't modeled.</summary>
    public float GetAudioSample()
    {
        float pulses = (_pulse1.Output + _pulse2.Output) / 30f;
        float saw = _sawtooth.Output / 31f;
        return (pulses + saw) * 0.5f;
    }

    public byte PpuRead(ushort address)
    {
        int bankCount1k = _chr.Length / 0x400;
        int bank = _chrBanks[address / 0x400] % bankCount1k;
        return _chr[bank * 0x400 + (address & 0x3FF)];
    }

    public void PpuWrite(ushort address, byte value)
    {
        if (!_chrIsRam)
        {
            return;
        }
        int bankCount1k = _chr.Length / 0x400;
        int bank = _chrBanks[address / 0x400] % bankCount1k;
        _chr[bank * 0x400 + (address & 0x3FF)] = value;
    }
}
