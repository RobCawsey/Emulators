using System.Text;
using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

/// <summary>
/// blargg's mmc3_irq_tests ROMs, using the standard $6000-status-byte convention his test
/// ROMs share (see TestRoms/README.md): $6000 holds $80 while running, and the final result
/// code once done ($00 = passed); human-readable diagnostic text is written starting at
/// $6004, null-terminated.
/// </summary>
public class Mmc3IrqBlarggTests
{
    private const long CycleBudget = 200_000_000;

    private static (byte ResultCode, string Text) RunTest(string romPath)
    {
        var cartridge = Cartridge.LoadFromInes(File.ReadAllBytes(romPath));
        var console = new NesConsole(cartridge);
        console.Reset();

        while (console.Bus.Read(0x6000) == 0x80 && console.Cpu.TotalCycles < CycleBudget)
        {
            console.Clock();
        }

        byte result = console.Bus.Read(0x6000);
        var text = new StringBuilder();
        for (ushort addr = 0x6004; addr < 0x6800; addr++)
        {
            byte b = console.Bus.Read(addr);
            if (b == 0)
            {
                break;
            }
            text.Append((char)b);
        }
        return (result, text.ToString());
    }

    [Theory]
    [InlineData("TestRoms/mmc3/1.Clocking.nes")]
    [InlineData("TestRoms/mmc3/2.Details.nes")]
    [InlineData("TestRoms/mmc3/3.A12_clocking.nes")]
    [InlineData("TestRoms/mmc3/4.Scanline_timing.nes")]
    public void PassesBlarggMmc3IrqTest(string romPath)
    {
        (byte result, string text) = RunTest(romPath);
        Assert.True(result == 0, $"{romPath} -> result code {result}:\n{text}");
    }
}
