using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class PsgTests
{
    /// <summary>Registers default to 0 = loudest, not muted -- real hardware has no implicit
    /// "silence at power-on" for volume, so a test isolating channel 0's tone needs to
    /// explicitly silence channels 1, 2, and noise or their (loud, by default) output bleeds
    /// into the mixed sample too.</summary>
    private static void SilenceOtherChannels(Psg psg)
    {
        psg.Write(0xBF); // channel 1 volume = max attenuation
        psg.Write(0xDF); // channel 2 volume = max attenuation
        psg.Write(0xFF); // noise volume = max attenuation
    }

    [Fact]
    public void ToneFrequency_CombinesLowNibbleAndHighSixBitsAcrossTwoWrites()
    {
        var psg = new Psg();

        psg.Write(0x8F); // latch register 0 (channel 0 tone), low 4 bits = 0xF
        psg.Write(0x2A); // bit7 clear -> high 6 bits = 0x2A

        Assert.Equal((0x2A << 4) | 0x0F, psg.ToneFrequency[0]);
    }

    [Fact]
    public void ToneFrequency_EachChannelIsIndependentlyAddressed()
    {
        var psg = new Psg();

        psg.Write(0x81); // register 0 (channel 0), low nibble 1
        psg.Write(0xA2); // register 2 (channel 1), low nibble 2
        psg.Write(0xC3); // register 4 (channel 2), low nibble 3

        Assert.Equal(1, psg.ToneFrequency[0]);
        Assert.Equal(2, psg.ToneFrequency[1]);
        Assert.Equal(3, psg.ToneFrequency[2]);
    }

    [Fact]
    public void Volume_IsSetDirectlyFromOneByte_NoSecondWriteNeeded()
    {
        var psg = new Psg();

        psg.Write(0x9A); // register 1 (channel 0 volume), data 0xA
        psg.Write(0xBC); // register 3 (channel 1 volume), data 0xC
        psg.Write(0xD5); // register 5 (channel 2 volume), data 0x5
        psg.Write(0xF7); // register 7 (noise volume), data 0x7

        Assert.Equal(0xA, psg.Volume[0]);
        Assert.Equal(0xC, psg.Volume[1]);
        Assert.Equal(0x5, psg.Volume[2]);
        Assert.Equal(0x7, psg.Volume[3]);
    }

    [Fact]
    public void NoiseControl_IsSetDirectlyFromRegisterSix()
    {
        var psg = new Psg();

        psg.Write(0xE5); // register 6 (noise control), data 0x5

        Assert.Equal(0x5, psg.NoiseControl);
    }

    [Fact]
    public void SecondByteWithoutALatchedToneRegister_IsIgnored()
    {
        var psg = new Psg();
        psg.Write(0xE5); // latches the noise register, not a tone register

        psg.Write(0x3F); // bit7 clear -- should have no effect, since register 6 isn't a tone register

        Assert.Equal(0, psg.ToneFrequency[0]);
        Assert.Equal(0, psg.ToneFrequency[1]);
        Assert.Equal(0, psg.ToneFrequency[2]);
    }

    [Fact]
    public void Reset_ClearsToneAndNoiseButSilencesVolume()
    {
        var psg = new Psg();
        psg.Write(0x8F);
        psg.Write(0x2A);
        psg.Write(0x9A);
        psg.Write(0xE5);

        psg.Reset();

        Assert.Equal(0, psg.ToneFrequency[0]);
        Assert.Equal(0, psg.NoiseControl);
        // Not 0 -- Sega's integrated PSG (unlike a generic discrete SN76489) is documented to
        // power on with its volume/attenuation registers already at max attenuation (silence),
        // the one register group where 0 would actually mean the opposite (loudest).
        Assert.Equal(0x0F, psg.Volume[0]);
        Assert.Equal(0x0F, psg.Volume[1]);
        Assert.Equal(0x0F, psg.Volume[2]);
        Assert.Equal(0x0F, psg.Volume[3]);
    }

    [Fact]
    public void GenerateSample_FirstSampleAtFullVolume_IsPositivePeakAmplitude()
    {
        // N=1023 (max 10-bit value) keeps the phase increment well under 0.5 for a single
        // sample at 44100Hz, so the very first sample lands cleanly in the "high" half of the
        // square wave -- a slow enough tone that the math is easy to hand-verify.
        var psg = new Psg();
        SilenceOtherChannels(psg);
        psg.Write(0x8F); // register 0 (channel 0 tone), low nibble 0xF
        psg.Write(0x3F); // high 6 bits 0x3F -> N = 0x3FF = 1023
        psg.Write(0x90); // register 1 (channel 0 volume) = 0, full volume

        short sample = psg.GenerateSample(44100);

        Assert.Equal(2500, sample); // PeakAmplitude at 0dB attenuation
    }

    [Fact]
    public void GenerateSample_MaxAttenuation_IsExactlySilent()
    {
        var psg = new Psg();
        SilenceOtherChannels(psg);
        psg.Write(0x8F);
        psg.Write(0x3F);
        psg.Write(0x9F); // volume register = 0xF, max attenuation

        short sample = psg.GenerateSample(44100);

        Assert.Equal(0, sample);
    }

    [Fact]
    public void GenerateSample_ToneRegisterZero_BehavesAsTheLowestNonZeroDivider_NotSilence()
    {
        var psg = new Psg();
        psg.Write(0x80); // N = 0
        psg.Write(0x90); // full volume

        short sample = psg.GenerateSample(44100);

        Assert.NotEqual(0, sample);
    }

    [Fact]
    public void GenerateSample_SquareWaveTogglesSignHalfwayThroughItsPeriod()
    {
        // N=1023 -> frequency = 3579545/(32*1023) ~= 109.3Hz -> period ~= 403.7 samples at
        // 44100Hz. Sampling at ~62% of the way through the period gives a comfortable margin
        // past the halfway point where the square wave flips from positive to negative.
        var psg = new Psg();
        SilenceOtherChannels(psg);
        psg.Write(0x8F);
        psg.Write(0x3F);
        psg.Write(0x90);

        short first = psg.GenerateSample(44100);
        short midpoint = 0;
        for (int i = 0; i < 249; i++)
        {
            midpoint = psg.GenerateSample(44100);
        }

        Assert.True(first > 0);
        Assert.True(midpoint < 0);
    }

    [Fact]
    public void GenerateSample_NoiseChannel_ProducesDeterministicOutputFromAFreshLfsrSeed()
    {
        var a = new Psg();
        var b = new Psg();
        a.Write(0xE0); b.Write(0xE0); // noise control: periodic, fastest fixed rate
        a.Write(0xF0); b.Write(0xF0); // full volume on the noise channel

        short[] sequenceA = new short[20];
        short[] sequenceB = new short[20];
        for (int i = 0; i < 20; i++)
        {
            sequenceA[i] = a.GenerateSample(44100);
            sequenceB[i] = b.GenerateSample(44100);
        }

        Assert.Equal(sequenceA, sequenceB); // two identically-configured PSGs must agree
    }

    [Fact]
    public void PeriodicNoise_HasAClean16StepRepeatFromAFreshLfsrSeed()
    {
        // A periodic-noise LFSR seeded to $8000 (bit 15 only) and fed back correctly into bit
        // 15 returns to exactly that state every 16 shifts. Feeding back into bit 14 instead
        // (an earlier version of this code did) permanently loses bit 15 after the very first
        // pass -- it settles into a *different*, 15-step steady-state cycle instead, so the
        // first 16-sample window would never exactly repeat. This distinguishes the two without
        // needing to reflect into the LFSR's internal state.
        var psg = new Psg();
        SilenceOtherChannels(psg);
        psg.Write(0xE0); // noise control: periodic, fastest fixed rate (register 6)
        psg.Write(0xF0); // noise volume = 0, loudest (register 7)

        // Fastest noise rate (0x10) shifts once every 0x10 * 32 = 512 PSG clocks; drive the
        // sample rate absurdly high relative to that so each GenerateSample call advances the
        // LFSR by exactly one shift, giving direct control over how many shifts we've captured.
        int sampleRateHz = (int)(3_579_545.0 / (32.0 * 0x10));
        var samples = new short[32];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = psg.GenerateSample(sampleRateHz);
        }

        Assert.Equal(samples[..16], samples[16..]);
    }

    [Fact]
    public void Reset_AlsoClearsSynthesisPhaseAndNoiseState()
    {
        var psg = new Psg();
        SilenceOtherChannels(psg);
        psg.Write(0x8F);
        psg.Write(0x3F);
        psg.Write(0x90);
        for (int i = 0; i < 50; i++) psg.GenerateSample(44100); // advance phase away from 0

        psg.Reset();
        SilenceOtherChannels(psg);
        psg.Write(0x8F);
        psg.Write(0x3F);
        psg.Write(0x90);

        Assert.Equal(2500, psg.GenerateSample(44100)); // back to the fresh-phase first-sample value
    }
}
