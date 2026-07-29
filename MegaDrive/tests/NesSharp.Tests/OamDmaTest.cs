using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class OamDmaTest
{
    private static Cartridge MinimalNromCartridge()
    {
        var rom = new byte[16 + 16384 + 8192];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = 1; // 16KB PRG
        rom[5] = 1; // 8KB CHR
        return Cartridge.LoadFromInes(rom);
    }

    [Fact]
    public void WritingOamDma_CopiesThePageIntoOam_AndStallsTheCpu()
    {
        var console = new NesConsole(MinimalNromCartridge());
        console.Reset();

        // Fill CPU page $02 with a recognizable pattern the DMA will copy into OAM.
        for (int i = 0; i < 256; i++)
        {
            console.Bus.Write((ushort)(0x0200 + i), (byte)(i ^ 0xA5));
        }

        long cyclesBefore = console.Cpu.TotalCycles;
        console.Bus.Write(0x4014, 0x02); // trigger OAMDMA from page $02

        Assert.True(console.Cpu.IsMidInstruction); // the stall cycles are now queued

        int stallCycles = 0;
        while (console.Cpu.IsMidInstruction)
        {
            console.Cpu.Clock();
            stallCycles++;
        }

        Assert.True(stallCycles is 513 or 514);
        Assert.Equal(cyclesBefore + stallCycles, console.Cpu.TotalCycles);

        console.Ppu.WriteRegister(3, 0x00); // OAMADDR = 0, to read OAM back from the start
        for (int i = 0; i < 256; i++)
        {
            byte expected = (byte)(i ^ 0xA5);
            byte actual = console.Ppu.ReadRegister(4);
            Assert.Equal(expected, actual);
            console.Ppu.WriteRegister(3, (byte)(i + 1)); // advance OAMADDR to read the next byte
        }
    }

    [Fact]
    public void OamDma_StallsForOddCycleCountWhenTriggeredOnAnOddCpuCycle()
    {
        var console = new NesConsole(MinimalNromCartridge());
        console.Reset();

        // NesConsole.Reset() leaves TotalCycles at 7 (odd) — write $4014 right at that point.
        Assert.Equal(7, console.Cpu.TotalCycles);
        console.Bus.Write(0x4014, 0x02);

        int stallCycles = 0;
        while (console.Cpu.IsMidInstruction)
        {
            console.Cpu.Clock();
            stallCycles++;
        }

        Assert.Equal(514, stallCycles); // odd trigger cycle -> the extra alignment cycle
    }
}
