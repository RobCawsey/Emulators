using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Mapper4Tests
{
    /// <summary>Builds a synthetic MMC3 cartridge where every 8KB PRG bank is filled with
    /// its own bank index and every 1KB CHR bank with 0x80+its index, so reads reveal
    /// exactly which bank is mapped where.</summary>
    private static Cartridge BuildCartridge(int prgBanks16k, int chrBanks8k)
    {
        int prgSize = prgBanks16k * 16384;
        int chrSize = chrBanks8k * 8192;
        var rom = new byte[16 + prgSize + chrSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = (byte)prgBanks16k;
        rom[5] = (byte)chrBanks8k;
        rom[6] = 0x40; // mapper low nibble = 4 (MMC3)
        rom[7] = 0x00;

        int prg8kBanks = prgSize / 0x2000;
        for (int bank = 0; bank < prg8kBanks; bank++)
        {
            Array.Fill(rom, (byte)bank, 16 + bank * 0x2000, 0x2000);
        }
        int chrOffsetBase = 16 + prgSize;
        int chr1kBanks = chrSize / 0x400;
        for (int bank = 0; bank < chr1kBanks; bank++)
        {
            Array.Fill(rom, (byte)(0x80 + bank), chrOffsetBase + bank * 0x400, 0x400);
        }

        return Cartridge.LoadFromInes(rom);
    }

    private static void SelectRegister(Mapper4 mapper, int registerIndex, byte extraSelectBits, byte value)
    {
        mapper.CpuWrite(0x8000, (byte)(extraSelectBits | registerIndex));
        mapper.CpuWrite(0x8001, value);
    }

    private static void ClockEdges(Mapper4 mapper, int count)
    {
        for (int i = 0; i < count; i++)
        {
            mapper.NotifyA12(0x0000);
            mapper.NotifyA12(0x1000);
        }
    }

    [Fact]
    public void PrgMode0_R6SwitchesFirstSlot_SecondLastAndLastAreFixed()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1)); // 4 8KB PRG banks: 0-3
        SelectRegister(mapper, 6, extraSelectBits: 0x00, value: 1); // mode 0 (bit6=0), R6 = bank 1

        Assert.Equal(1, mapper.CpuRead(0x8000)); // R6
        Assert.Equal(2, mapper.CpuRead(0xC000)); // fixed: second-last (bank 2 of 0-3)
        Assert.Equal(3, mapper.CpuRead(0xE000)); // fixed: last
    }

    [Fact]
    public void PrgMode1_R6SwitchesThirdSlot_FirstIsFixedToSecondLast()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1));
        SelectRegister(mapper, 6, extraSelectBits: 0x40, value: 1); // mode 1 (bit6=1), R6 = bank 1

        Assert.Equal(2, mapper.CpuRead(0x8000)); // fixed: second-last
        Assert.Equal(1, mapper.CpuRead(0xC000)); // R6
        Assert.Equal(3, mapper.CpuRead(0xE000)); // fixed: last
    }

    [Fact]
    public void R7_AlwaysMapsSecondSlot_RegardlessOfPrgMode()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1));
        SelectRegister(mapper, 7, extraSelectBits: 0x00, value: 2);

        Assert.Equal(2, mapper.CpuRead(0xA000));
    }

    [Fact]
    public void ChrNormalMode_R0AndR1AreTwoKBPairs_R2ThroughR5AreOneKB()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1)); // 8 1KB CHR banks: 0x80-0x87
        SelectRegister(mapper, 0, 0x00, 2); // R0 = bank 2 (low bit ignored anyway)
        SelectRegister(mapper, 1, 0x00, 4);
        SelectRegister(mapper, 2, 0x00, 0x81);
        SelectRegister(mapper, 3, 0x00, 0x82);
        SelectRegister(mapper, 4, 0x00, 0x83);
        SelectRegister(mapper, 5, 0x00, 0x84);

        Assert.Equal(0x80 + 2, mapper.PpuRead(0x0000)); // R0 first half
        Assert.Equal(0x80 + 3, mapper.PpuRead(0x0400)); // R0 second half
        Assert.Equal(0x80 + 4, mapper.PpuRead(0x0800)); // R1 first half
        Assert.Equal(0x80 + 5, mapper.PpuRead(0x0C00)); // R1 second half
        Assert.Equal(0x81, mapper.PpuRead(0x1000)); // R2
        Assert.Equal(0x82, mapper.PpuRead(0x1400)); // R3
        Assert.Equal(0x83, mapper.PpuRead(0x1800)); // R4
        Assert.Equal(0x84, mapper.PpuRead(0x1C00)); // R5
    }

    [Fact]
    public void ChrInvertedMode_SwapsTheTwoHalves()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0x8000, 0x80); // bank-select with bit7 set (CHR inversion) targeting R0
        mapper.CpuWrite(0x8001, 2); // R0 = bank 2
        SelectRegister(mapper, 1, 0x80, 4);
        SelectRegister(mapper, 2, 0x80, 0x81);
        SelectRegister(mapper, 3, 0x80, 0x82);
        SelectRegister(mapper, 4, 0x80, 0x83);
        SelectRegister(mapper, 5, 0x80, 0x84);

        // Inverted: R2-R5 now occupy $0000-$0FFF, R0/R1 occupy $1000-$1FFF.
        Assert.Equal(0x81, mapper.PpuRead(0x0000));
        Assert.Equal(0x82, mapper.PpuRead(0x0400));
        Assert.Equal(0x83, mapper.PpuRead(0x0800));
        Assert.Equal(0x84, mapper.PpuRead(0x0C00));
        Assert.Equal(0x80 + 2, mapper.PpuRead(0x1000));
        Assert.Equal(0x80 + 3, mapper.PpuRead(0x1400));
        Assert.Equal(0x80 + 4, mapper.PpuRead(0x1800));
        Assert.Equal(0x80 + 5, mapper.PpuRead(0x1C00));
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.Vertical)]
    [InlineData((byte)0x01, MirroringMode.Horizontal)]
    public void MirroringRegister_SelectsExpectedMode(byte value, MirroringMode expected)
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xA000, value);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void PrgRam_Disabled_ByDefault()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0x6000, 0x42);

        Assert.Equal(0x00, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void PrgRam_EnabledAndWritable_WhenControlBitsSetAccordingly()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xA001, 0x80); // enable, writable

        mapper.CpuWrite(0x6000, 0x42);
        Assert.Equal(0x42, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void PrgRam_WriteProtected_IgnoresWritesButStillReadable()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xA001, 0x80); // enable, writable
        mapper.CpuWrite(0x6000, 0x42);
        mapper.CpuWrite(0xA001, 0xC0); // enable, write-protected

        mapper.CpuWrite(0x6000, 0x99); // should be ignored now

        Assert.Equal(0x42, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void ChrRam_PersistsWrites_WhenCartridgeHasNoChrRom()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 0));
        mapper.PpuWrite(0x0055, 0x77);

        Assert.Equal(0x77, mapper.PpuRead(0x0055));
    }

    [Fact]
    public void IrqCounter_FiresAfterExactlyLatchPlusOneEdges_WhenEnabled()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xC000, 4); // latch = 4
        mapper.CpuWrite(0xC001, 0); // force a reload on the next edge
        mapper.CpuWrite(0xE001, 0); // enable IRQ

        ClockEdges(mapper, 1); // reload: counter = 4
        Assert.False(mapper.IrqLine);

        ClockEdges(mapper, 3); // 4 -> 3 -> 2 -> 1
        Assert.False(mapper.IrqLine);

        ClockEdges(mapper, 1); // 1 -> 0
        Assert.True(mapper.IrqLine);
    }

    [Fact]
    public void IrqCounter_ReachingZero_DoesNotFire_WhenDisabled()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xC000, 2);
        mapper.CpuWrite(0xC001, 0);
        // Never write $E001 -> IRQ stays disabled.

        ClockEdges(mapper, 3); // reload to 2, then 2->1->0

        Assert.False(mapper.IrqLine);
    }

    [Fact]
    public void WritingE000_AcknowledgesPendingIrqAndDisablesFutureOnes()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xC000, 1);
        mapper.CpuWrite(0xC001, 0);
        mapper.CpuWrite(0xE001, 0);
        ClockEdges(mapper, 2); // reload to 1, then 1->0 (pending)
        Assert.True(mapper.IrqLine);

        mapper.CpuWrite(0xE000, 0); // acknowledge + disable

        Assert.False(mapper.IrqLine);

        mapper.CpuWrite(0xC001, 0);
        ClockEdges(mapper, 2); // would fire again if still enabled
        Assert.False(mapper.IrqLine);
    }

    [Fact]
    public void WritingC001_ForcesReloadOnNextEdge_EvenMidCountdown()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xC000, 10);
        mapper.CpuWrite(0xC001, 0);
        mapper.CpuWrite(0xE001, 0);

        ClockEdges(mapper, 1); // reload -> 10
        ClockEdges(mapper, 3); // 10 -> 9 -> 8 -> 7

        mapper.CpuWrite(0xC001, 0); // request reload again, counter is currently 7

        // The next edge reloads (counter=10, that edge doesn't also decrement), so reaching
        // zero takes 11 edges total from here — not 7 (continuing the old countdown) or 10.
        ClockEdges(mapper, 10);
        Assert.False(mapper.IrqLine);
        ClockEdges(mapper, 1);
        Assert.True(mapper.IrqLine);
    }

    [Fact]
    public void NotifyA12_OnlyClocksOnRisingEdge()
    {
        var mapper = new Mapper4(BuildCartridge(prgBanks16k: 1, chrBanks8k: 1));
        mapper.CpuWrite(0xC000, 1);
        mapper.CpuWrite(0xC001, 0);
        mapper.CpuWrite(0xE001, 0);

        mapper.NotifyA12(0x1000); // rising edge: reload -> counter = 1
        mapper.NotifyA12(0x1000); // still high — not an edge, must be ignored

        Assert.False(mapper.IrqLine); // would be true if the second call incorrectly clocked it
    }
}
