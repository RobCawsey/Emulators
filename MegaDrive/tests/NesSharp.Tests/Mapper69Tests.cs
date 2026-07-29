using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Mapper69Tests
{
    private static Cartridge BuildCartridge(int prg8kBanks, int chr1kBanks)
    {
        int prgSize = prg8kBanks * 8192;
        int chrSize = chr1kBanks * 1024;
        var rom = new byte[16 + prgSize + chrSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = (byte)(prgSize / 16384);
        rom[5] = (byte)(chrSize / 8192);
        const int mapperNumber = 69;
        rom[6] = (byte)((mapperNumber & 0x0F) << 4);
        rom[7] = (byte)(mapperNumber & 0xF0);

        for (int b = 0; b < prg8kBanks; b++)
        {
            Array.Fill(rom, (byte)b, 16 + b * 8192, 8192);
        }
        int chrBase = 16 + prgSize;
        for (int b = 0; b < chr1kBanks; b++)
        {
            Array.Fill(rom, (byte)(0x80 + b), chrBase + b * 1024, 1024);
        }
        return Cartridge.LoadFromInes(rom);
    }

    private static void SelectAndWrite(Mapper69 mapper, byte register, byte value)
    {
        mapper.CpuWrite(0x8000, register);
        mapper.CpuWrite(0xA000, value);
    }

    [Fact]
    public void PrgBanking_ThreeIndependentSlots_FixedLastBank()
    {
        var mapper = new Mapper69(BuildCartridge(prg8kBanks: 8, chr1kBanks: 8));
        SelectAndWrite(mapper, 9, 2); // $8000 -> bank 2
        SelectAndWrite(mapper, 10, 4); // $A000 -> bank 4
        SelectAndWrite(mapper, 11, 6); // $C000 -> bank 6

        Assert.Equal(2, mapper.CpuRead(0x8000));
        Assert.Equal(4, mapper.CpuRead(0xA000));
        Assert.Equal(6, mapper.CpuRead(0xC000));
        Assert.Equal(7, mapper.CpuRead(0xE000)); // fixed: last bank
    }

    [Fact]
    public void SixThousandRegion_CanBeSwitchableRom()
    {
        var mapper = new Mapper69(BuildCartridge(prg8kBanks: 4, chr1kBanks: 8));
        SelectAndWrite(mapper, 8, 0x02); // bit7=0 (ROM), bank=2

        Assert.Equal(2, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void SixThousandRegion_CanBeEnabledRam()
    {
        var mapper = new Mapper69(BuildCartridge(prg8kBanks: 4, chr1kBanks: 8));
        SelectAndWrite(mapper, 8, 0xC0); // bit7=1 (RAM), bit6=1 (enabled)

        mapper.CpuWrite(0x6000, 0x55);
        Assert.Equal(0x55, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void SixThousandRegion_DisabledRam_ReadsZeroAndIgnoresWrites()
    {
        var mapper = new Mapper69(BuildCartridge(prg8kBanks: 4, chr1kBanks: 8));
        SelectAndWrite(mapper, 8, 0x80); // bit7=1 (RAM), bit6=0 (disabled)

        mapper.CpuWrite(0x6000, 0x55);
        Assert.Equal(0x00, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void ChrBanking_EightIndependentOneKBBanks()
    {
        var mapper = new Mapper69(BuildCartridge(prg8kBanks: 2, chr1kBanks: 16));
        SelectAndWrite(mapper, 0, 3);
        SelectAndWrite(mapper, 7, 9);

        Assert.Equal(0x83, mapper.PpuRead(0x0000));
        Assert.Equal(0x89, mapper.PpuRead(0x1C00));
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.Vertical)]
    [InlineData((byte)0x01, MirroringMode.Horizontal)]
    [InlineData((byte)0x02, MirroringMode.SingleScreenLower)]
    [InlineData((byte)0x03, MirroringMode.SingleScreenUpper)]
    public void MirroringRegister_SelectsExpectedMode(byte value, MirroringMode expected)
    {
        var mapper = new Mapper69(BuildCartridge(2, 8));
        SelectAndWrite(mapper, 12, value);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void Irq_CountsDownAndFiresOnUnderflow()
    {
        var mapper = new Mapper69(BuildCartridge(2, 8));
        SelectAndWrite(mapper, 14, 0x02); // counter low = 2
        SelectAndWrite(mapper, 15, 0x00); // counter high = 0 -> counter = 2
        SelectAndWrite(mapper, 13, 0x81); // enable IRQ + counter

        mapper.ClockCpu(); // 2 -> 1
        Assert.False(mapper.IrqLine);
        mapper.ClockCpu(); // 1 -> 0
        Assert.False(mapper.IrqLine);
        mapper.ClockCpu(); // 0 -> underflow -> 0xFFFF, pending
        Assert.True(mapper.IrqLine);
    }

    [Fact]
    public void Irq_WritingControlRegister_Acknowledges()
    {
        var mapper = new Mapper69(BuildCartridge(2, 8));
        SelectAndWrite(mapper, 14, 0x00);
        SelectAndWrite(mapper, 15, 0x00);
        SelectAndWrite(mapper, 13, 0x81); // counter starts at 0 -> immediate underflow next clock
        mapper.ClockCpu();
        Assert.True(mapper.IrqLine);

        SelectAndWrite(mapper, 13, 0x81); // rewriting the control register acknowledges
        Assert.False(mapper.IrqLine);
    }

    [Fact]
    public void Psg_ToneChannel_TogglesAndContributesWhenEnabledAndAudible()
    {
        var mapper = new Mapper69(BuildCartridge(2, 8));
        mapper.CpuWrite(0xC000, 0); // select tone A period low
        mapper.CpuWrite(0xE000, 1); // short period
        mapper.CpuWrite(0xC000, 7); // mixer
        mapper.CpuWrite(0xE000, 0xFE); // enable tone A (bit0=0), disable everything else
        mapper.CpuWrite(0xC000, 8); // channel A volume
        mapper.CpuWrite(0xE000, 0x0F); // fixed volume 15, no envelope

        var samples = new List<float>();
        for (int i = 0; i < 400; i++) // several PSG clocks (1 per 16 CPU cycles)
        {
            mapper.ClockCpu();
            samples.Add(mapper.GetAudioSample());
        }

        Assert.Contains(0f, samples); // tone's "off" half contributes silence for this channel
        Assert.True(samples.Max() > 0f); // tone's "on" half contributes audible output
    }

    [Fact]
    public void Psg_ZeroVolume_IsSilentEvenWhenChannelIsOn()
    {
        var mapper = new Mapper69(BuildCartridge(2, 8));
        mapper.CpuWrite(0xC000, 7);
        mapper.CpuWrite(0xE000, 0xFF); // everything disabled -> all channels constantly "on"
        mapper.CpuWrite(0xC000, 8);
        mapper.CpuWrite(0xE000, 0x00); // volume 0
        mapper.CpuWrite(0xC000, 9);
        mapper.CpuWrite(0xE000, 0x00);
        mapper.CpuWrite(0xC000, 10);
        mapper.CpuWrite(0xE000, 0x00);

        for (int i = 0; i < 20; i++)
        {
            mapper.ClockCpu();
        }

        Assert.Equal(0f, mapper.GetAudioSample());
    }

    [Fact]
    public void ChrRam_PersistsWrites_WhenCartridgeHasNoChrRom()
    {
        var mapper = new Mapper69(BuildCartridge(prg8kBanks: 2, chr1kBanks: 0));
        mapper.PpuWrite(0x0033, 0x66);

        Assert.Equal(0x66, mapper.PpuRead(0x0033));
    }
}
