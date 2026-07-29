using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class GenesisConsoleSaveStateTests
{
    private static string FindSagaRomsDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "SagaRoms");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("SagaRoms not found walking up from " + AppContext.BaseDirectory);
    }

    private static GenesisConsole CreateConsole()
    {
        var romPath = Path.Combine(FindSagaRomsDir(), "sonic.bin");
        var cartridge = Cartridge.LoadFromBin(File.ReadAllBytes(romPath));
        var console = new GenesisConsole(cartridge);
        console.Reset();
        return console;
    }

    /// <summary>The strongest possible check: run a real ROM for a while, save, load that same
    /// save into a *second*, freshly-constructed console for the same ROM, then run both
    /// consoles forward the same number of further frames. If the snapshot genuinely captured
    /// every piece of state that affects future behavior, the two consoles must produce
    /// bit-identical frame buffers and audio from that point on -- any field this doesn't
    /// actually save (or restores wrong) would eventually show up as a divergence here.</summary>
    [Fact]
    public void SaveThenLoadIntoFreshConsole_ProducesIdenticalFutureFramesAndAudio()
    {
        var original = CreateConsole();
        for (int frame = 0; frame < 120; frame++)
        {
            original.RunFrame();
        }

        using var stream = new MemoryStream();
        original.SaveState(stream);

        // LoadState clears the restored side's buffered-audio queue (see its remarks) --
        // drain the original's own queue the same way so the comparison below starts from an
        // equally-empty queue on both sides, rather than the original having a head start of
        // samples generated (but never dequeued) during the frames run before the save.
        while (original.TryDequeueBufferedAudioSample(out _, out _))
        {
        }

        var restored = CreateConsole();
        stream.Position = 0;
        restored.LoadState(stream);

        for (int frame = 0; frame < 60; frame++)
        {
            original.RunFrame();
            restored.RunFrame();

            Assert.Equal(original.Vdp.FrameBuffer, restored.Vdp.FrameBuffer);

            while (original.TryDequeueBufferedAudioSample(out short oLeft, out short oRight))
            {
                Assert.True(restored.TryDequeueBufferedAudioSample(out short rLeft, out short rRight));
                Assert.Equal(oLeft, rLeft);
                Assert.Equal(oRight, rRight);
            }
        }
    }

    [Fact]
    public void SaveState_RoundTripsIntoTheSameConsoleWithoutChangingBehavior()
    {
        var console = CreateConsole();
        for (int frame = 0; frame < 60; frame++)
        {
            console.RunFrame();
        }

        using var stream = new MemoryStream();
        console.SaveState(stream);

        var beforeReload = new byte[console.Vdp.FrameBuffer.Length];
        Array.Copy(console.Vdp.FrameBuffer, beforeReload, beforeReload.Length);

        stream.Position = 0;
        console.LoadState(stream);

        Assert.Equal(beforeReload, console.Vdp.FrameBuffer);

        // Continuing to run after a self-reload shouldn't throw or desync -- just a basic
        // liveness check that state wasn't left in a way that breaks stepping.
        console.RunFrame();
    }

    [Fact]
    public void LoadState_RejectsAMismatchedRomByDefault()
    {
        var sonic = CreateConsole();
        using var stream = new MemoryStream();
        sonic.SaveState(stream);

        var otherRomPath = Path.Combine(FindSagaRomsDir(), "omega_blast.bin");
        var otherConsole = new GenesisConsole(Cartridge.LoadFromBin(File.ReadAllBytes(otherRomPath)));
        otherConsole.Reset();

        stream.Position = 0;
        Assert.Throws<SaveStateRomMismatchException>(() => otherConsole.LoadState(stream));
    }

    [Fact]
    public void LoadState_AllowsAMismatchedRomWhenExplicitlyRequested()
    {
        var sonic = CreateConsole();
        using var stream = new MemoryStream();
        sonic.SaveState(stream);

        var otherRomPath = Path.Combine(FindSagaRomsDir(), "omega_blast.bin");
        var otherConsole = new GenesisConsole(Cartridge.LoadFromBin(File.ReadAllBytes(otherRomPath)));
        otherConsole.Reset();

        stream.Position = 0;
        otherConsole.LoadState(stream, allowRomMismatch: true);
    }

    [Fact]
    public void LoadState_RejectsAFileThatIsNotASaveState()
    {
        var console = CreateConsole();
        using var stream = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Throws<InvalidDataException>(() => console.LoadState(stream));
    }
}
