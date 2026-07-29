namespace NesSharp.Core;

/// <summary>
/// VRC7 (Konami, mapper 85) — Lagrange Point, one of the rarest mappers in the licensed
/// library. PRG/CHR banking and the IRQ counter follow the same VRC-family design as VRC6
/// (see <see cref="Mapper24"/>) at similar confidence. The defining feature is expansion
/// audio: 6 FM synthesis channels compatible in spirit with the YM2413/OPLL chip family,
/// selecting from 15 preset "instrument" patches plus one user-programmable custom patch.
///
/// <b>This is the lowest-confidence audio implementation in the project, by a wide margin.</b>
/// Real OPLL hardware's exact 15 instrument patches are ROM-baked parameter sets (specific
/// multiplier ratios, envelope rates, feedback amounts) that would need to be reproduced
/// byte-for-byte to sound like the real chip, and its sine wave comes from a log-domain ROM
/// table rather than a plain sine function. Neither is reproduced here — this implements
/// genuine, working 2-operator FM synthesis (see <see cref="Vrc7FmOperator"/> and
/// <see cref="Vrc7FmChannel"/>) with an invented set of 15 patches for timbral variety, not
/// an attempt to match real VRC7 instrument sounds. The register layout (which addresses
/// map to which of the chip's functions) follows the commonly documented VRC7 address map at
/// reasonable confidence; the exact bit-packing within the per-channel control registers is
/// a simplified reconstruction.
/// </summary>
public sealed class Mapper85 : IMapper
{
    private readonly record struct Vrc7Patch(
        byte ModMultiplier, byte CarMultiplier, byte ModTotalLevel, byte Feedback,
        byte AttackRate, byte DecayRate, byte SustainLevel, byte ReleaseRate, bool SustainMode);

    // Invented for timbral variety — not real VRC7/OPLL instrument ROM data. See class doc.
    private static readonly Vrc7Patch[] Patches =
    {
        default,
        new(1, 1, 20, 0, 15, 8, 12, 8, false),
        new(2, 1, 24, 1, 14, 7, 10, 7, false),
        new(1, 2, 16, 2, 12, 6, 14, 6, true),
        new(3, 1, 28, 0, 15, 10, 8, 9, false),
        new(1, 3, 20, 3, 10, 5, 12, 5, false),
        new(2, 2, 18, 1, 13, 9, 10, 8, true),
        new(4, 1, 30, 0, 15, 12, 6, 10, false),
        new(1, 4, 22, 2, 11, 6, 13, 6, false),
        new(2, 3, 26, 4, 9, 8, 9, 7, false),
        new(3, 2, 24, 1, 14, 9, 11, 8, true),
        new(1, 1, 10, 5, 15, 4, 15, 4, false),
        new(5, 1, 32, 0, 15, 14, 4, 12, false),
        new(1, 5, 20, 3, 8, 5, 10, 5, false),
        new(3, 3, 28, 2, 12, 8, 8, 7, false),
        new(4, 2, 26, 1, 13, 10, 9, 9, true),
    };

    private const double SampleRateHz = 1789773.0 / 16.0;

    private readonly byte[] _prg;
    private readonly byte[] _chr;
    private readonly bool _chrIsRam;
    private readonly byte[] _prgRam = new byte[8 * 1024];

    private byte _prgBank0;
    private byte _prgBank1;
    private byte _prgBank2;
    private readonly byte[] _chrBanks = new byte[8];
    private byte _controlRegister; // mirroring (bits0-1), WRAM enable (bit7), sound disable (bit6)

    private byte _irqLatch;
    private bool _irqEnabled;
    private bool _irqCycleMode;
    private bool _irqPending;
    private byte _irqCounter;
    private int _irqPrescaler = 341;

    private readonly Vrc7FmChannel[] _channels = { new(), new(), new(), new(), new(), new() };
    private readonly byte[] _channelPatchIndex = new byte[6];
    private byte _audioSelectedRegister;

    // Custom (patch 0) parameters, shared by whichever channels select patch 0.
    private byte _customModMultiplier = 1;
    private byte _customCarMultiplier = 1;
    private byte _customModTotalLevel;
    private byte _customFeedback;
    private byte _customModAttack, _customModDecay, _customModSustain, _customModRelease;
    private byte _customCarAttack, _customCarDecay, _customCarSustain, _customCarRelease;
    private bool _customModSustainMode, _customCarSustainMode;
    private byte _customWaveformMod, _customWaveformCar;

    public Mapper85(Cartridge cartridge)
    {
        _prg = cartridge.Prg;
        _chrIsRam = cartridge.Chr.Length == 0;
        _chr = _chrIsRam ? new byte[8 * 1024] : cartridge.Chr;
    }

    public MirroringMode Mirroring => (_controlRegister & 0x03) switch
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
            return (_controlRegister & 0x80) != 0 ? _prgRam[address - 0x6000] : (byte)0;
        }
        if (address < 0x8000)
        {
            return 0;
        }
        int bankCount8k = _prg.Length / 0x2000;
        int bank = address switch
        {
            < 0xA000 => _prgBank0 % bankCount8k,
            < 0xC000 => _prgBank1 % bankCount8k,
            < 0xE000 => _prgBank2 % bankCount8k,
            _ => bankCount8k - 1,
        };
        return _prg[bank * 0x2000 + (address & 0x1FFF)];
    }

    public void CpuWrite(ushort address, byte value)
    {
        if (address is >= 0x6000 and < 0x8000)
        {
            if ((_controlRegister & 0x80) != 0)
            {
                _prgRam[address - 0x6000] = value;
            }
            return;
        }
        if (address < 0x8000)
        {
            return;
        }

        switch (address & 0xFFF0)
        {
            case 0x8000: _prgBank0 = value; break;
            case 0x8010: _prgBank1 = value; break;
            case 0x9000: _prgBank2 = value; break;
            case 0x9010: _audioSelectedRegister = (byte)(value & 0x3F); break;
            case 0x9030: WriteAudioRegister(value); break;
            case 0xA000: _chrBanks[0] = value; break;
            case 0xA010: _chrBanks[1] = value; break;
            case 0xB000: _chrBanks[2] = value; break;
            case 0xB010: _chrBanks[3] = value; break;
            case 0xC000: _chrBanks[4] = value; break;
            case 0xC010: _chrBanks[5] = value; break;
            case 0xD000: _chrBanks[6] = value; break;
            case 0xD010: _chrBanks[7] = value; break;
            case 0xE000: _controlRegister = value; break;
            case 0xF000: _irqLatch = value; break;
            case 0xF010:
                _irqEnabled = (value & 0x01) != 0;
                _irqCycleMode = (value & 0x02) != 0;
                if (_irqEnabled)
                {
                    _irqCounter = _irqLatch;
                    _irqPrescaler = 341;
                }
                _irqPending = false;
                break;
            case 0xF020:
                _irqPending = false;
                break;
        }
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

    public void ClockCpu()
    {
        foreach (Vrc7FmChannel channel in _channels)
        {
            channel.Clock(SampleRateHz);
        }

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

    private void WriteAudioRegister(byte value)
    {
        if (_audioSelectedRegister <= 0x07)
        {
            WriteCustomPatchRegister(_audioSelectedRegister, value);
            return;
        }
        if (_audioSelectedRegister is >= 0x10 and <= 0x15)
        {
            int ch = _audioSelectedRegister - 0x10;
            _channels[ch].FNumber = (ushort)((_channels[ch].FNumber & 0x100) | value);
            return;
        }
        if (_audioSelectedRegister is >= 0x20 and <= 0x25)
        {
            int ch = _audioSelectedRegister - 0x20;
            _channels[ch].FNumber = (ushort)((_channels[ch].FNumber & 0x0FF) | ((value & 0x01) << 8));
            _channels[ch].SetKeyOn((value & 0x10) != 0);
            _channels[ch].Block = (byte)((value >> 6) & 0x03);
            return;
        }
        if (_audioSelectedRegister is >= 0x30 and <= 0x35)
        {
            int ch = _audioSelectedRegister - 0x30;
            _channels[ch].Volume = (byte)((value >> 4) & 0x0F);
            ApplyPatch(ch, (byte)(value & 0x0F));
        }
    }

    private void WriteCustomPatchRegister(byte reg, byte value)
    {
        switch (reg)
        {
            case 0x00: _customModMultiplier = (byte)Math.Max(1, value & 0x0F); _customModSustainMode = (value & 0x20) != 0; break;
            case 0x01: _customCarMultiplier = (byte)Math.Max(1, value & 0x0F); _customCarSustainMode = (value & 0x20) != 0; break;
            case 0x02: _customModTotalLevel = (byte)(value & 0x3F); break;
            case 0x03:
                _customFeedback = (byte)(value & 0x07);
                _customWaveformMod = (byte)((value >> 3) & 0x01);
                _customWaveformCar = (byte)((value >> 4) & 0x01);
                break;
            case 0x04: _customModAttack = (byte)((value >> 4) & 0x0F); _customModDecay = (byte)(value & 0x0F); break;
            case 0x05: _customCarAttack = (byte)((value >> 4) & 0x0F); _customCarDecay = (byte)(value & 0x0F); break;
            case 0x06: _customModSustain = (byte)((value >> 4) & 0x0F); _customModRelease = (byte)(value & 0x0F); break;
            case 0x07: _customCarSustain = (byte)((value >> 4) & 0x0F); _customCarRelease = (byte)(value & 0x0F); break;
        }

        for (int ch = 0; ch < 6; ch++)
        {
            if (_channelPatchIndex[ch] == 0)
            {
                ApplyPatch(ch, 0);
            }
        }
    }

    private void ApplyPatch(int channelIndex, byte patchIndex)
    {
        _channelPatchIndex[channelIndex] = patchIndex;
        Vrc7FmChannel channel = _channels[channelIndex];

        if (patchIndex == 0)
        {
            channel.Modulator.Multiplier = _customModMultiplier;
            channel.Carrier.Multiplier = _customCarMultiplier;
            channel.Modulator.TotalLevel = _customModTotalLevel;
            channel.Carrier.TotalLevel = 0;
            channel.Feedback = _customFeedback;
            channel.Modulator.Waveform = _customWaveformMod;
            channel.Carrier.Waveform = _customWaveformCar;
            channel.Modulator.AttackRate = _customModAttack;
            channel.Modulator.DecayRate = _customModDecay;
            channel.Modulator.SustainLevel = _customModSustain;
            channel.Modulator.ReleaseRate = _customModRelease;
            channel.Modulator.SustainMode = _customModSustainMode;
            channel.Carrier.AttackRate = _customCarAttack;
            channel.Carrier.DecayRate = _customCarDecay;
            channel.Carrier.SustainLevel = _customCarSustain;
            channel.Carrier.ReleaseRate = _customCarRelease;
            channel.Carrier.SustainMode = _customCarSustainMode;
            return;
        }

        Vrc7Patch patch = Patches[Math.Min(patchIndex, (byte)(Patches.Length - 1))];
        channel.Modulator.Multiplier = patch.ModMultiplier;
        channel.Carrier.Multiplier = patch.CarMultiplier;
        channel.Modulator.TotalLevel = patch.ModTotalLevel;
        channel.Carrier.TotalLevel = 0;
        channel.Feedback = patch.Feedback;
        channel.Modulator.AttackRate = patch.AttackRate;
        channel.Modulator.DecayRate = patch.DecayRate;
        channel.Modulator.SustainLevel = patch.SustainLevel;
        channel.Modulator.ReleaseRate = patch.ReleaseRate;
        channel.Modulator.SustainMode = patch.SustainMode;
        channel.Carrier.AttackRate = patch.AttackRate;
        channel.Carrier.DecayRate = patch.DecayRate;
        channel.Carrier.SustainLevel = patch.SustainLevel;
        channel.Carrier.ReleaseRate = patch.ReleaseRate;
        channel.Carrier.SustainMode = patch.SustainMode;
    }

    /// <summary>Sums the 6 FM channels, normalized and modestly weighted like the other
    /// expansion-audio mappers; silenced entirely when the control register's sound-disable
    /// bit is set.</summary>
    public float GetAudioSample()
    {
        if ((_controlRegister & 0x40) != 0)
        {
            return 0f;
        }
        float sum = 0f;
        foreach (Vrc7FmChannel channel in _channels)
        {
            sum += channel.Output();
        }
        return sum / 6f * 0.5f;
    }
}
