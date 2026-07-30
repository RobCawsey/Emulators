namespace GenesisSharp.Core;

/// <summary>SN76489 audio synthesis: 3 square-wave tone generators plus one LFSR-based noise
/// generator, mixed down to one mono sample. The tone generator's math (frequency = clock /
/// (32 * N), 2dB-per-step attenuation) is widely and consistently documented for this chip and
/// recalled with decent confidence. The noise LFSR (16-bit register, feedback into bit 15 from
/// a bit0-XOR-bit3 tap in "white" mode or a plain bit0 tap in "periodic" mode) is verified
/// against smspower.org's SN76489 reference for the SMS/Genesis/Game Gear variant specifically.</summary>
public sealed partial class Psg
{
    // ~3.579545 MHz -- the Z80's own NTSC clock domain, which the PSG shares on Genesis
    // hardware (the classic "3.58MHz" figure commonly cited for Sega's SN76489 wiring).
    private const double ClockHz = 3_579_545.0;

    // Kept well under Int16 range so 4 channels at full volume, mixed with the YM2612's own
    // budget (see Ym2612.Synthesis.cs), can't overflow before the final explicit clamp does.
    private const int PeakAmplitude = 2500;

    private readonly double[] _tonePhase = new double[3];
    private uint _noiseLfsr = 0x8000;
    private double _noisePhase;

    private void ResetSynthesisState()
    {
        Array.Clear(_tonePhase);
        _noiseLfsr = 0x8000;
        _noisePhase = 0;
    }

    /// <summary>One mono sample. Real PSG output is mono — <see cref="GenesisConsole"/> mixes
    /// it into both stereo channels equally alongside the (stereo-capable) YM2612.</summary>
    public short GenerateSample(int sampleRateHz)
    {
        int mix = 0;

        for (int channel = 0; channel < 3; channel++)
        {
            // Register value 0 behaves as the lowest nonzero divider on real hardware, not
            // "silent" -- there's no dedicated "channel off" state for the tone generator
            // itself, only via the volume/attenuation register.
            int n = ToneFrequency[channel] == 0 ? 1 : ToneFrequency[channel];
            double frequencyHz = ClockHz / (32.0 * n);

            _tonePhase[channel] += frequencyHz / sampleRateHz;
            _tonePhase[channel] -= Math.Floor(_tonePhase[channel]);

            bool high = _tonePhase[channel] < 0.5;
            mix += (high ? 1 : -1) * ChannelAmplitude(Volume[channel]);
        }

        mix += AdvanceNoise(sampleRateHz) * ChannelAmplitude(Volume[3]);

        return (short)Math.Clamp(mix, short.MinValue, short.MaxValue);
    }

    /// <summary>4-bit attenuation, 0 = loudest, -2dB per step; 15 is treated as exact silence
    /// rather than the last -2dB step, matching how most SN76489 documentation describes the
    /// real DAC's floor.</summary>
    private static int ChannelAmplitude(byte attenuation) =>
        attenuation >= 15 ? 0 : (int)(PeakAmplitude * Math.Pow(10, -attenuation * 2.0 / 20.0));

    /// <summary>The noise channel's shift rate is either one of three fixed divisors or (rate
    /// select == 3) locked to tone channel 2's own frequency.</summary>
    private int AdvanceNoise(int sampleRateHz)
    {
        int rateSelect = NoiseControl & 0x03;
        double shiftFrequencyHz = rateSelect switch
        {
            0 => ClockHz / (32.0 * 0x10),
            1 => ClockHz / (32.0 * 0x20),
            2 => ClockHz / (32.0 * 0x40),
            _ => ClockHz / (32.0 * (ToneFrequency[2] == 0 ? 1 : ToneFrequency[2])),
        };

        _noisePhase += shiftFrequencyHz / sampleRateHz;
        while (_noisePhase >= 1.0)
        {
            _noisePhase -= 1.0;
            // 16-bit LFSR, feedback into bit 15 -- confirmed against smspower.org's SN76489
            // reference specifically for the SMS/Genesis/Game Gear variant. This used to insert
            // into bit 14, one bit short of the register width _noiseLfsr's own 0x8000 initial
            // value already implied (that top bit would immediately shift away and never get
            // replenished), degrading the register to an effectively-15-bit LFSR with a shorter
            // repeat period and different texture than real hardware's noise channel.
            bool white = (NoiseControl & 0x04) != 0;
            uint feedback = white ? ((_noiseLfsr ^ (_noiseLfsr >> 3)) & 1) : (_noiseLfsr & 1);
            _noiseLfsr = (_noiseLfsr >> 1) | (feedback << 15);
        }

        return (_noiseLfsr & 1) != 0 ? 1 : -1;
    }
}
