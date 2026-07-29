using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

/// <summary>
/// Runs a real ROM from its actual reset vector (not nestest's CPU-only automation mode)
/// through several frames of CPU+PPU+bus interaction together — NMI-driven timing, PPU
/// register polling, and rendering all exercised at once. Not a substitute for the
/// byte-exact NestestLogTest or the focused Ppu2C02 unit tests; this just catches "the
/// whole system wedges/crashes/produces nothing" failures that isolated tests can't.
/// </summary>
public class FullSystemSmokeTest
{
    [Fact]
    public void Nestest_RunsSeveralFramesFromRealResetVector_AndRendersSomething()
    {
        byte[] romBytes = File.ReadAllBytes("TestRoms/nestest.nes");
        var cartridge = Cartridge.LoadFromInes(romBytes);
        var console = new NesConsole(cartridge);

        console.Reset(); // uses the ROM's actual reset vector this time, not $C000

        long targetFrame = console.Ppu.FrameCount + 5;
        long safetyCycleLimit = console.Cpu.TotalCycles + 20_000_000;
        while (console.Ppu.FrameCount < targetFrame && console.Cpu.TotalCycles < safetyCycleLimit)
        {
            console.Clock();
        }

        Assert.True(console.Ppu.FrameCount >= targetFrame,
            $"Only reached frame {console.Ppu.FrameCount} of {targetFrame} within the cycle budget — " +
            "likely stuck (e.g. spin-waiting on a PPU flag that never changes).");

        var distinctColors = new HashSet<byte>(console.Ppu.Frame);
        Assert.True(distinctColors.Count > 1,
            "Frame buffer has only one distinct color — nothing appears to have rendered.");
    }
}
