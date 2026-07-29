using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class Ym2612SynthesisTests
{
    private const int Channel = 0;

    private static void WriteChannelRegister(Ym2612 ym, int address, byte value)
    {
        ym.WriteAddressPart1((byte)(address + Channel));
        ym.WriteDataPart1(value);
    }

    // Real hardware's per-operator register offsets aren't in "natural" S1,S2,S3,S4 order --
    // it's S1,S3,S2,S4 (offsets 0,4,8,12) -- so a logical slot number (matching how every
    // published algorithm diagram numbers operators, and how Ym2612.Synthesis.cs's
    // AlgorithmModulationSources/AlgorithmCarriers tables are indexed) needs the same
    // translation here that Ym2612Synthesis.OperatorRegister applies in production.
    private static readonly int[] SlotRegisterOffset = { 0, 8, 4, 12 };

    private static void WriteOperatorRegister(Ym2612 ym, int slot, int group, byte value) =>
        WriteChannelRegister(ym, group + SlotRegisterOffset[slot], value);

    private static void KeyOn(Ym2612 ym, int slotMask)
    {
        ym.WriteAddressPart1(0x28);
        ym.WriteDataPart1((byte)((slotMask << 4) | Channel));
    }

    private static void KeyOff(Ym2612 ym)
    {
        ym.WriteAddressPart1(0x28);
        ym.WriteDataPart1(Channel); // slot mask 0 -> all operators off
    }

    /// <summary>Frequency (block 4, fnum 1000) + algorithm 7 (all 4 operators independent
    /// carriers) + full total level + fastest attack on every slot, panned to both channels.
    /// A reusable "should definitely make sound" baseline for tests that don't care about the
    /// specific timbre.</summary>
    private static void ConfigureAudibleTone(Ym2612 ym, int algorithm = 7)
    {
        WriteChannelRegister(ym, 0xA4, 0x23); // block=4, fnum bits8-10=3
        WriteChannelRegister(ym, 0xA0, 0xE8); // fnum bits0-7 -> fnum=0x3E8=1000
        WriteChannelRegister(ym, 0xB0, (byte)algorithm);
        WriteChannelRegister(ym, 0xB4, 0xC0); // L+R enabled
        for (int slot = 0; slot < 4; slot++)
        {
            WriteOperatorRegister(ym, slot, 0x40, 0);    // TL=0, loudest
            WriteOperatorRegister(ym, slot, 0x50, 0x1F); // AR=31, fastest attack
        }
    }

    [Fact]
    public void KeyOn_WithFrequencyAndFullVolume_EventuallyProducesAudibleOutput()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym);
        KeyOn(ym, 0x0F);

        int maxAbs = 0;
        for (int i = 0; i < 2000; i++)
        {
            (short left, _) = ym.GenerateSample(44100);
            maxAbs = Math.Max(maxAbs, Math.Abs((int)left));
        }

        Assert.True(maxAbs > 100);
    }

    [Fact]
    public void WithoutKeyOn_StaysSilent()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym);
        // deliberately no KeyOn call

        int maxAbs = 0;
        for (int i = 0; i < 2000; i++)
        {
            (short left, _) = ym.GenerateSample(44100);
            maxAbs = Math.Max(maxAbs, Math.Abs((int)left));
        }

        Assert.Equal(0, maxAbs);
    }

    [Fact]
    public void KeyOff_EventuallyDecaysToSilence()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym);
        KeyOn(ym, 0x0F);

        for (int i = 0; i < 2000; i++) ym.GenerateSample(44100); // reach a steady sounding state

        for (int slot = 0; slot < 4; slot++)
        {
            WriteOperatorRegister(ym, slot, 0x80, 0x0F); // SL=0, RR=15 (fastest release)
        }
        KeyOff(ym);

        short lastLeft = 0;
        for (int i = 0; i < 10000; i++)
        {
            (lastLeft, _) = ym.GenerateSample(44100);
        }

        Assert.True(Math.Abs((int)lastLeft) < 50);
    }

    [Fact]
    public void Algorithm_ChangesTheOutputWaveform()
    {
        static short[] Capture(int algorithm)
        {
            var ym = new Ym2612();
            ConfigureAudibleTone(ym, algorithm);
            for (int slot = 0; slot < 4; slot++)
            {
                WriteOperatorRegister(ym, slot, 0x30, (byte)(slot + 1)); // distinct multiples so operators differ
            }
            KeyOn(ym, 0x0F);

            var samples = new short[500];
            for (int i = 0; i < samples.Length; i++)
            {
                (samples[i], _) = ym.GenerateSample(44100);
            }
            return samples;
        }

        Assert.NotEqual(Capture(0), Capture(7));
    }

    [Fact]
    public void WriteOperatorRegister_UsesHardwareSlotOrder_S1S3S2S4NotNaturalOrder()
    {
        // Register offset +4 is physically Slot 3's data and +8 is Slot 2's -- the opposite of
        // what a naive "slot * 4" mapping would assume. This pins that translation down
        // directly against raw register reads, independent of any audio-output assertion, so a
        // regression here can't hide behind a symmetric test setup the way the other tests in
        // this file would (they write the same value to every slot).
        var ym = new Ym2612();
        WriteOperatorRegister(ym, slot: 0, group: 0x30, value: 0x11);
        WriteOperatorRegister(ym, slot: 1, group: 0x30, value: 0x22);
        WriteOperatorRegister(ym, slot: 2, group: 0x30, value: 0x33);
        WriteOperatorRegister(ym, slot: 3, group: 0x30, value: 0x44);

        Assert.Equal(0x11, ym.ReadRegisterPart1(0x30 + 0));  // Slot 1
        Assert.Equal(0x33, ym.ReadRegisterPart1(0x30 + 4));  // Slot 3, not Slot 2
        Assert.Equal(0x22, ym.ReadRegisterPart1(0x30 + 8));  // Slot 2, not Slot 3
        Assert.Equal(0x44, ym.ReadRegisterPart1(0x30 + 12)); // Slot 4
    }

    [Fact]
    public void PanRegister_RoutesOutputToOnlyTheSelectedChannel()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym);
        WriteChannelRegister(ym, 0xB4, 0x80); // Left only
        KeyOn(ym, 0x0F);

        bool everNonZeroLeft = false;
        bool everNonZeroRight = false;
        for (int i = 0; i < 1000; i++)
        {
            (short left, short right) = ym.GenerateSample(44100);
            if (left != 0) everNonZeroLeft = true;
            if (right != 0) everNonZeroRight = true;
        }

        Assert.True(everNonZeroLeft);
        Assert.False(everNonZeroRight);
    }

    [Fact]
    public void MaxTotalLevel_ProducesEffectiveSilenceRegardlessOfEnvelope()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym);
        for (int slot = 0; slot < 4; slot++)
        {
            WriteOperatorRegister(ym, slot, 0x40, 0x7F); // max total level attenuation
        }
        KeyOn(ym, 0x0F);

        int maxAbs = 0;
        for (int i = 0; i < 500; i++)
        {
            (short left, _) = ym.GenerateSample(44100);
            maxAbs = Math.Max(maxAbs, Math.Abs((int)left));
        }

        Assert.True(maxAbs <= 1); // rounds down to (near) nothing at ~127dB of attenuation
    }

    [Fact]
    public void Reset_SilencesAllChannelsAndClearsKeyState()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym);
        KeyOn(ym, 0x0F);
        for (int i = 0; i < 500; i++) ym.GenerateSample(44100);

        ym.Reset();

        (short left, short right) = ym.GenerateSample(44100);
        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void IdenticalConfiguration_ProducesDeterministicOutput()
    {
        var a = new Ym2612();
        var b = new Ym2612();
        ConfigureAudibleTone(a);
        ConfigureAudibleTone(b);
        KeyOn(a, 0x0F);
        KeyOn(b, 0x0F);

        for (int i = 0; i < 200; i++)
        {
            Assert.Equal(a.GenerateSample(44100), b.GenerateSample(44100));
        }
    }

    // ---- DAC/PCM playback (channel 6, registers 0x2A/0x2B) -------------------------------

    private static void WriteDacSample(Ym2612 ym, byte value)
    {
        ym.WriteAddressPart1(0x2A);
        ym.WriteDataPart1(value);
    }

    private static void SetDacEnabled(Ym2612 ym, bool enabled)
    {
        ym.WriteAddressPart1(0x2B);
        ym.WriteDataPart1(enabled ? (byte)0x80 : (byte)0x00);
    }

    /// <summary>Channel 6's pan register lives in the Part II bank at 0xB4 + (channel % 3) =
    /// 0xB6 -- same physical register FM would use for that channel, since DAC output still
    /// routes through it on real hardware.</summary>
    private static void SetDacChannelPan(Ym2612 ym, byte value)
    {
        ym.WriteAddressPart2(0xB6);
        ym.WriteDataPart2(value);
    }

    [Fact]
    public void Dac_WhenEnabled_OutputsTheHeldSampleInsteadOfSilence()
    {
        var ym = new Ym2612();
        SetDacChannelPan(ym, 0xC0); // L+R
        WriteDacSample(ym, 0xFF); // max positive PCM value
        SetDacEnabled(ym, true);

        (short left, short right) = ym.GenerateSample(44100);

        Assert.True(left > 0);
        Assert.Equal(left, right);
    }

    [Fact]
    public void Dac_HeldSampleBelowCenter_ProducesNegativeOutput()
    {
        var ym = new Ym2612();
        SetDacChannelPan(ym, 0xC0);
        WriteDacSample(ym, 0x00); // min PCM value, below the 0x80 center
        SetDacEnabled(ym, true);

        (short left, _) = ym.GenerateSample(44100);

        Assert.True(left < 0);
    }

    [Fact]
    public void Dac_CenteredSample_IsSilent()
    {
        var ym = new Ym2612();
        SetDacChannelPan(ym, 0xC0);
        WriteDacSample(ym, 0x80); // the DAC's own silent/centered level
        SetDacEnabled(ym, true);

        (short left, short right) = ym.GenerateSample(44100);

        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void Dac_Disabled_FallsBackToChannel6sUnconfiguredFmOutput()
    {
        var ym = new Ym2612();
        SetDacChannelPan(ym, 0xC0);
        WriteDacSample(ym, 0xFF);
        SetDacEnabled(ym, true);
        SetDacEnabled(ym, false); // re-disable -- channel 6 was never set up for FM either

        (short left, short right) = ym.GenerateSample(44100);

        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void Dac_RespectsItsOwnPanRegister()
    {
        var ym = new Ym2612();
        SetDacChannelPan(ym, 0x80); // Left only
        WriteDacSample(ym, 0xFF);
        SetDacEnabled(ym, true);

        (short left, short right) = ym.GenerateSample(44100);

        Assert.True(left > 0);
        Assert.Equal(0, right);
    }

    [Fact]
    public void Dac_DoesNotAffectOtherChannels()
    {
        var ym = new Ym2612();
        ConfigureAudibleTone(ym); // channel 0
        KeyOn(ym, 0x0F);
        SetDacChannelPan(ym, 0xC0);
        WriteDacSample(ym, 0xFF);
        SetDacEnabled(ym, true);

        int maxAbs = 0;
        for (int i = 0; i < 2000; i++)
        {
            (short left, _) = ym.GenerateSample(44100);
            maxAbs = Math.Max(maxAbs, Math.Abs((int)left));
        }

        // Channel 0's normal FM envelope/attack still ramps up to an audible level even with
        // the DAC concurrently driving channel 6's output into the same stereo mix.
        Assert.True(maxAbs > 100);
    }

    [Fact]
    public void Reset_ClearsDacStateAndDisablesIt()
    {
        var ym = new Ym2612();
        SetDacChannelPan(ym, 0xC0);
        WriteDacSample(ym, 0xFF);
        SetDacEnabled(ym, true);

        ym.Reset();
        SetDacChannelPan(ym, 0xC0); // Reset also clears the pan register -- restore it to isolate the DAC-state assertion

        (short left, short right) = ym.GenerateSample(44100);

        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }
}
