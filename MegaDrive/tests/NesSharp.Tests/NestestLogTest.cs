using System.Text.RegularExpressions;
using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

/// <summary>
/// Replays nestest.nes from its documented "automation mode" entry point ($C000, which
/// needs no PPU/APU interaction) and checks every logged instruction in nestest.log against
/// our own CPU+NesBus+Mapper0 state: PC, registers, PPU dot/scanline, and CPU cycle count.
/// This is the milestone-3 completion gate — a byte-exact match against a real-hardware
/// capture, covering both official and undocumented opcodes. See TestRoms/README.md.
/// </summary>
public class NestestLogTest
{
    private static readonly Regex LineRegex = new(
        @"^(?<pc>[0-9A-F]{4}).*A:(?<a>[0-9A-F]{2}) X:(?<x>[0-9A-F]{2}) Y:(?<y>[0-9A-F]{2}) " +
        @"P:(?<p>[0-9A-F]{2}) SP:(?<sp>[0-9A-F]{2}) PPU:\s*(?<sl>\d+),\s*(?<dot>\d+) CYC:(?<cyc>\d+)$",
        RegexOptions.Compiled);

    private sealed record ExpectedState(ushort Pc, byte A, byte X, byte Y, byte P, byte Sp, int Scanline, int Dot, long Cycles, int LineNumber, string RawLine);

    [Fact]
    public void MatchesRealHardwareLogForEveryInstruction()
    {
        byte[] romBytes = File.ReadAllBytes("TestRoms/nestest.nes");
        var cartridge = Cartridge.LoadFromInes(romBytes);
        var console = new NesConsole(cartridge);

        console.Reset();
        console.Cpu.PC = 0xC000; // nestest's automation-mode entry point

        string[] logLines = File.ReadAllLines("TestRoms/nestest.log");
        List<ExpectedState> expectations = ParseLog(logLines);
        Assert.NotEmpty(expectations);

        for (int i = 0; i < expectations.Count; i++)
        {
            ExpectedState expected = expectations[i];
            AssertState(expected, console);
            console.StepInstruction();
        }
    }

    private static void AssertState(ExpectedState expected, NesConsole console)
    {
        var cpu = console.Cpu;
        string context = $"log line {expected.LineNumber}: \"{expected.RawLine}\"";

        Assert.True(expected.Pc == cpu.PC, $"{context} — PC expected 0x{expected.Pc:X4}, was 0x{cpu.PC:X4}");
        Assert.True(expected.A == cpu.A, $"{context} — A expected 0x{expected.A:X2}, was 0x{cpu.A:X2}");
        Assert.True(expected.X == cpu.X, $"{context} — X expected 0x{expected.X:X2}, was 0x{cpu.X:X2}");
        Assert.True(expected.Y == cpu.Y, $"{context} — Y expected 0x{expected.Y:X2}, was 0x{cpu.Y:X2}");
        Assert.True(expected.P == (byte)cpu.P, $"{context} — P expected 0x{expected.P:X2}, was 0x{(byte)cpu.P:X2}");
        Assert.True(expected.Sp == cpu.SP, $"{context} — SP expected 0x{expected.Sp:X2}, was 0x{cpu.SP:X2}");
        Assert.True(expected.Scanline == console.Ppu.Scanline, $"{context} — PPU scanline expected {expected.Scanline}, was {console.Ppu.Scanline}");
        Assert.True(expected.Dot == console.Ppu.Dot, $"{context} — PPU dot expected {expected.Dot}, was {console.Ppu.Dot}");
        Assert.True(expected.Cycles == cpu.TotalCycles, $"{context} — CYC expected {expected.Cycles}, was {cpu.TotalCycles}");
    }

    private static List<ExpectedState> ParseLog(string[] lines)
    {
        var result = new List<ExpectedState>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0)
            {
                continue;
            }
            Match m = LineRegex.Match(line);
            if (!m.Success)
            {
                throw new FormatException($"Couldn't parse nestest.log line {i + 1}: \"{line}\"");
            }
            result.Add(new ExpectedState(
                Pc: Convert.ToUInt16(m.Groups["pc"].Value, 16),
                A: Convert.ToByte(m.Groups["a"].Value, 16),
                X: Convert.ToByte(m.Groups["x"].Value, 16),
                Y: Convert.ToByte(m.Groups["y"].Value, 16),
                P: Convert.ToByte(m.Groups["p"].Value, 16),
                Sp: Convert.ToByte(m.Groups["sp"].Value, 16),
                Scanline: int.Parse(m.Groups["sl"].Value),
                Dot: int.Parse(m.Groups["dot"].Value),
                Cycles: long.Parse(m.Groups["cyc"].Value),
                LineNumber: i + 1,
                RawLine: line));
        }
        return result;
    }
}
