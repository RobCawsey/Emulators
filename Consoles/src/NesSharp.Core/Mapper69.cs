namespace NesSharp.Core;

/// <summary>
/// Sunsoft FME-7 / 5B (mapper 69) — Gimmick! (5B, with expansion audio), Batman: Return of
/// the Joker. A command/data register pair ($8000=select, $A000=data) reaches 12 internal
/// registers: eight independent 1KB CHR banks, three 8KB PRG-ROM banks ($8000/$A000/$C000 —
/// $E000-$FFFF is always fixed to the last bank), a dual-purpose $6000-$7FFF region that can
/// be either PRG-RAM or another switchable 8KB PRG-ROM bank, mirroring control, and a
/// 16-bit down-counting IRQ that fires on underflow (count *down* to wraparound — a
/// different mechanism from VRC6's count-up-to-overflow).
///
/// The 5B variant's expansion audio is a 3-channel PSG compatible with the classic General
/// Instrument AY-3-8910: three tone generators, a shared noise generator, a mixer enabling
/// tone/noise per channel, and either fixed volume or a shared envelope generator per
/// channel. Reached through a separate register pair ($C000=select, $E000=data, distinct
/// from the banking command/data pair). The tone/noise/envelope generators are clocked once
/// per 16 CPU cycles (the commonly cited master clock divider for this chip on the NES).
///
/// Confidence notes: banking, mirroring, the IRQ, and PSG tone/noise/mixer/fixed-volume are
/// implemented at reasonably high confidence from the documented specs. The 16-code envelope
/// shape table (attack/alternate/hold/continue) is a best-effort reconstruction of the
/// AY-3-8910 datasheet's behavior, not verified against a reference — Sunsoft games are
/// understood to mostly use fixed volume rather than the envelope, limiting how much this
/// affects real playback. The exact volume-to-amplitude curve (real AY-3-8910 hardware uses
/// a specific, roughly logarithmic 16-step table) is approximated with a formula, not the
/// measured hardware values.
/// </summary>
public sealed class Mapper69 : IMapper
{
    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[8 * 1024];

    private byte _selectedRegister;
    private readonly byte[] _chrBanks = new byte[8];
    private byte _prgBank6000;
    private byte _prgBank8000;
    private byte _prgBankA000;
    private byte _prgBankC000;
    private byte _mirroringBits;

    private bool _irqCounterEnabled;
    private bool _irqEnabled;
    private ushort _irqCounter;
    private bool _irqPending;

    // ---- PSG (5B expansion audio) ----
    private byte _psgSelectedRegister;
    private readonly ushort[] _tonePeriod = new ushort[3];
    private readonly ushort[] _toneCounter = new ushort[3];
    private readonly bool[] _toneOutput = new bool[3];
    private readonly byte[] _volume = new byte[3];
    private readonly bool[] _useEnvelope = new bool[3];
    private byte _mixer = 0xFF; // AY power-on default: everything disabled (active-low)
    private byte _noisePeriod;
    private ushort _noiseCounter;
    private uint _noiseLfsr = 1;
    private bool _noiseOutput;
    private ushort _envelopePeriod;
    private ushort _envelopeCounter;
    private byte _envelopeShape;
    private int _envelopeStep;
    private byte _psgMasterDivider;

    public Mapper69(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => (_mirroringBits & 0x03) switch
    {
        0 => MirroringMode.Vertical,
        1 => MirroringMode.Horizontal,
        2 => MirroringMode.SingleScreenLower,
        _ => MirroringMode.SingleScreenUpper,
    };

    public bool IrqLine => _irqPending;

    // ---- CPU bus ----

    public byte CpuRead(ushort address)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            bool isRam = (_prgBank6000 & 0x80) != 0;
            if (isRam)
            {
                bool enabled = (_prgBank6000 & 0x40) != 0;
                return enabled ? _prgRam[address - 0x6000] : (byte)0;
            }
            int bankCount8k = _prg.Length / 0x2000;
            int bank = (_prgBank6000 & 0x3F) % bankCount8k;
            return _prg[bank * 0x2000 + (address - 0x6000)];
        }
        if (address < 0x8000)
        {
            return 0;
        }

        int prgBankCount = _prg.Length / 0x2000;
        int selectedBank = address switch
        {
            < 0xA000 => _prgBank8000 % prgBankCount,
            < 0xC000 => _prgBankA000 % prgBankCount,
            < 0xE000 => _prgBankC000 % prgBankCount,
            _ => prgBankCount - 1,
        };
        return _prg[selectedBank * 0x2000 + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_prgBank6000 & 0xC0) == 0xC0) // RAM selected and enabled
            {
                _prgRam[address - 0x6000] = value;
            }
            return;
        }
        if (address < 0x8000)
        {
            return;
        }

        switch (address & 0xE000)
        {
            case 0x8000:
                _selectedRegister = (byte)(value & 0x0F);
                break;
            case 0xA000:
                WriteSelectedRegister(value);
                break;
            case 0xC000:
                _psgSelectedRegister = (byte)(value & 0x0F);
                break;
            case 0xE000:
                WritePsgRegister(value);
                break;
        }
    }

    private void WriteSelectedRegister(byte value)
    {
        switch (_selectedRegister)
        {
            case <= 7:
                _chrBanks[_selectedRegister] = value;
                break;
            case 8:
                _prgBank6000 = value;
                break;
            case 9:
                _prgBank8000 = value;
                break;
            case 10:
                _prgBankA000 = value;
                break;
            case 11:
                _prgBankC000 = value;
                break;
            case 12:
                _mirroringBits = value;
                break;
            case 13:
                _irqEnabled = (value & 0x01) != 0;
                _irqCounterEnabled = (value & 0x80) != 0;
                _irqPending = false;
                break;
            case 14:
                _irqCounter = (ushort)((_irqCounter & 0xFF00) | value);
                break;
            case 15:
                _irqCounter = (ushort)((_irqCounter & 0x00FF) | (value << 8));
                break;
        }
    }

    public void ClockCpu()
    {
        if (_irqCounterEnabled)
        {
            if (_irqCounter == 0)
            {
                _irqCounter = 0xFFFF;
                if (_irqEnabled)
                {
                    _irqPending = true;
                }
            }
            else
            {
                _irqCounter--;
            }
        }

        _psgMasterDivider++;
        if (_psgMasterDivider >= 16)
        {
            _psgMasterDivider = 0;
            ClockPsg();
        }
    }

    // ---- PPU bus ----

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

    // ---- PSG registers ----

    private void WritePsgRegister(byte value)
    {
        switch (_psgSelectedRegister)
        {
            case 0: _tonePeriod[0] = (ushort)((_tonePeriod[0] & 0x0F00) | value); break;
            case 1: _tonePeriod[0] = (ushort)((_tonePeriod[0] & 0x00FF) | ((value & 0x0F) << 8)); break;
            case 2: _tonePeriod[1] = (ushort)((_tonePeriod[1] & 0x0F00) | value); break;
            case 3: _tonePeriod[1] = (ushort)((_tonePeriod[1] & 0x00FF) | ((value & 0x0F) << 8)); break;
            case 4: _tonePeriod[2] = (ushort)((_tonePeriod[2] & 0x0F00) | value); break;
            case 5: _tonePeriod[2] = (ushort)((_tonePeriod[2] & 0x00FF) | ((value & 0x0F) << 8)); break;
            case 6: _noisePeriod = (byte)(value & 0x1F); break;
            case 7: _mixer = value; break;
            case 8: _volume[0] = (byte)(value & 0x0F); _useEnvelope[0] = (value & 0x10) != 0; break;
            case 9: _volume[1] = (byte)(value & 0x0F); _useEnvelope[1] = (value & 0x10) != 0; break;
            case 10: _volume[2] = (byte)(value & 0x0F); _useEnvelope[2] = (value & 0x10) != 0; break;
            case 11: _envelopePeriod = (ushort)((_envelopePeriod & 0xFF00) | value); break;
            case 12: _envelopePeriod = (ushort)((_envelopePeriod & 0x00FF) | (value << 8)); break;
            case 13: _envelopeShape = (byte)(value & 0x0F); _envelopeStep = 0; break;
        }
    }

    private void ClockPsg()
    {
        for (int ch = 0; ch < 3; ch++)
        {
            if (_toneCounter[ch] == 0)
            {
                _toneCounter[ch] = _tonePeriod[ch] == 0 ? (ushort)1 : _tonePeriod[ch];
                _toneOutput[ch] = !_toneOutput[ch];
            }
            else
            {
                _toneCounter[ch]--;
            }
        }

        if (_noiseCounter == 0)
        {
            _noiseCounter = _noisePeriod == 0 ? (byte)1 : _noisePeriod;
            uint feedback = (_noiseLfsr & 1) ^ ((_noiseLfsr >> 3) & 1);
            _noiseLfsr = (_noiseLfsr >> 1) | (feedback << 16);
            _noiseOutput = (_noiseLfsr & 1) != 0;
        }
        else
        {
            _noiseCounter--;
        }

        if (_envelopeCounter == 0)
        {
            _envelopeCounter = _envelopePeriod == 0 ? (ushort)1 : _envelopePeriod;
            _envelopeStep = (_envelopeStep + 1) % 32;
        }
        else
        {
            _envelopeCounter--;
        }
    }

    private byte EnvelopeLevel()
    {
        bool continueShape = (_envelopeShape & 0x08) != 0;
        bool attack = (_envelopeShape & 0x04) != 0;
        bool alternate = (_envelopeShape & 0x02) != 0;
        bool hold = (_envelopeShape & 0x01) != 0;

        int step = _envelopeStep;
        if (!continueShape)
        {
            step = Math.Min(step, 15);
            if (_envelopeStep >= 16 && hold)
            {
                return 0;
            }
            return (byte)(attack ? step : 15 - step);
        }

        bool secondPass = step >= 16;
        int cycleStep = step % 16;
        bool descending = attack ? (alternate && secondPass) : !(alternate && secondPass);
        return (byte)(descending ? 15 - cycleStep : cycleStep);
    }

    /// <summary>Normalizes the three PSG channels to roughly the same 0..~1 scale as the
    /// 2A03 mixer's own output, with a modest overall weight so expansion audio doesn't
    /// overpower the built-in channels. The per-step amplitude curve is an exponential
    /// approximation of the AY-3-8910's roughly-logarithmic volume table, not the measured
    /// hardware values.</summary>
    public float GetAudioSample()
    {
        byte envelopeLevel = (_useEnvelope[0] || _useEnvelope[1] || _useEnvelope[2]) ? EnvelopeLevel() : (byte)0;
        float sum = 0f;
        for (int ch = 0; ch < 3; ch++)
        {
            bool toneEnabled = (_mixer & (1 << ch)) == 0;
            bool noiseEnabled = (_mixer & (1 << (3 + ch))) == 0;
            bool toneSignal = toneEnabled ? _toneOutput[ch] : true;
            bool noiseSignal = noiseEnabled ? _noiseOutput : true;
            if (toneSignal && noiseSignal)
            {
                byte level = _useEnvelope[ch] ? envelopeLevel : _volume[ch];
                sum += level == 0 ? 0f : MathF.Pow(2f, (level - 15) / 2f);
            }
        }
        return sum / 3f * 0.5f;
    }
}
