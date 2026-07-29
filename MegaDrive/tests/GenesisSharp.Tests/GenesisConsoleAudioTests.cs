using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class GenesisConsoleAudioTests
{
    [Fact]
    public void GenerateAudioSample_WithEverythingExplicitlySilenced_IsSilent()
    {
        // The YM2612 defaults to silent on its own (FNUM=0 means its oscillators never even
        // advance), but the PSG's volume registers default to 0 = *loudest*, not muted --
        // real hardware has no implicit "quiet at power-on," so a genuinely silent mix needs
        // every PSG channel explicitly attenuated.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        console.Psg.Write(0x9F); // channel 0 volume: max attenuation
        console.Psg.Write(0xBF); // channel 1
        console.Psg.Write(0xDF); // channel 2
        console.Psg.Write(0xFF); // noise

        (short left, short right) = console.GenerateAudioSample();

        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void GenerateAudioSample_MixesPsgEquallyIntoBothChannels()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        console.Psg.Write(0x8F); // channel 0 tone, low nibble
        console.Psg.Write(0x3F); // high 6 bits -> N = 1023 (slow enough for a clean first sample)
        console.Psg.Write(0x90); // full volume

        (short left, short right) = console.GenerateAudioSample();

        // The YM2612 is silent (nothing configured), so the mix should be pure PSG, identical
        // on both channels since real PSG output is mono.
        Assert.Equal(left, right);
        Assert.NotEqual(0, left);
    }

    [Fact]
    public void GenerateAudioSample_DoesNotThrowAcrossManyCalls()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        console.Psg.Write(0x8F);
        console.Psg.Write(0x00);
        console.Psg.Write(0x90);

        for (int i = 0; i < 5000; i++)
        {
            console.GenerateAudioSample();
        }
    }

    // ---- Cycle-accurate inline buffering (RunScanline feeding TryDequeueBufferedAudioSample) ----

    [Fact]
    public void TryDequeueBufferedAudioSample_ReturnsFalseAndSilence_BeforeAnyFrameHasRun()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();

        bool hadSample = console.TryDequeueBufferedAudioSample(out short left, out short right);

        Assert.False(hadSample);
        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }

    [Fact]
    public void RunFrame_BuffersApproximatelyOneNtscFramesWorthOfSamples()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();

        console.RunFrame();

        int count = 0;
        while (console.TryDequeueBufferedAudioSample(out _, out _))
        {
            count++;
        }

        // 44100Hz / (~59.92 fps) ~= 735.7 samples/frame -- a couple of samples either way from
        // sub-scanline rounding is expected and fine, but the buffer existing at all (rather
        // than needing a manual GenerateAudioSample call) and roughly matching one frame's
        // worth is exactly the new contract RunFrame provides.
        Assert.InRange(count, 730, 740);
    }

    [Fact]
    public void RunFrame_BufferedOutput_MatchesDirectlyGeneratedSampleForTheSameConfiguration()
    {
        // Not a test of intra-frame timing (that needs 68000/Z80 register writes mid-frame,
        // which the public API doesn't expose a way to inject at a precise scanline) -- just
        // confirms the new buffered path produces the same kind of correct, audible output the
        // already-trusted direct GenerateAudioSample path does for an unchanging configuration,
        // so the buffering plumbing itself isn't silently corrupting or muting samples.
        var direct = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        direct.Reset();
        direct.Psg.Write(0x8F); // channel 0 tone, low nibble
        direct.Psg.Write(0x3F); // high 6 bits -> N = 1023
        direct.Psg.Write(0x90); // full volume
        (short directLeft, _) = direct.GenerateAudioSample();

        var buffered = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        buffered.Reset();
        buffered.Psg.Write(0x8F);
        buffered.Psg.Write(0x3F);
        buffered.Psg.Write(0x90);
        buffered.RunFrame();
        buffered.TryDequeueBufferedAudioSample(out short bufferedLeft, out _);

        Assert.NotEqual(0, directLeft);
        Assert.Equal(directLeft, bufferedLeft); // first sample of a fresh-phase tone is deterministic either way
    }

    [Fact]
    public void Reset_ClearsAnyBufferedAudio()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        console.RunFrame();

        console.Reset();

        Assert.False(console.TryDequeueBufferedAudioSample(out short left, out short right));
        Assert.Equal(0, left);
        Assert.Equal(0, right);
    }
}
