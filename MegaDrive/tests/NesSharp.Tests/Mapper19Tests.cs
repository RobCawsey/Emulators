using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Mapper19Tests
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
        const int mapperNumber = 19;
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

    private static void WriteSoundRam(Mapper19 mapper, byte address, byte value)
    {
        mapper.CpuWrite(0xF800, address); // no auto-increment
        mapper.CpuWrite(0x4800, value);
    }

    [Fact]
    public void PrgBanking_ThreeSwitchableSlots_FixedLastBank()
    {
        var mapper = new Mapper19(BuildCartridge(prg8kBanks: 8, chr1kBanks: 8));
        mapper.CpuWrite(0xE000, 2);
        mapper.CpuWrite(0xE800, 4);
        mapper.CpuWrite(0xF000, 6);

        Assert.Equal(2, mapper.CpuRead(0x8000));
        Assert.Equal(4, mapper.CpuRead(0xA000));
        Assert.Equal(6, mapper.CpuRead(0xC000));
        Assert.Equal(7, mapper.CpuRead(0xE000));
    }

    [Fact]
    public void ChrBanking_EightIndependentOneKBBanks()
    {
        var mapper = new Mapper19(BuildCartridge(prg8kBanks: 2, chr1kBanks: 16));
        mapper.CpuWrite(0x8000, 3); // CHR bank 0
        mapper.CpuWrite(0xB800, 9); // CHR bank 7

        Assert.Equal(0x83, mapper.PpuRead(0x0000));
        Assert.Equal(0x89, mapper.PpuRead(0x1C00));
    }

    [Fact]
    public void Mirroring_InfersHorizontalFromMatchingNametableBankPairs()
    {
        var mapper = new Mapper19(BuildCartridge(2, 8));
        mapper.CpuWrite(0xC000, 0xE0); // quadrant 0 -> CIRAM page 0
        mapper.CpuWrite(0xC800, 0xE0); // quadrant 1 -> CIRAM page 0
        mapper.CpuWrite(0xD000, 0xE1); // quadrant 2 -> CIRAM page 1
        mapper.CpuWrite(0xD800, 0xE1); // quadrant 3 -> CIRAM page 1

        Assert.Equal(MirroringMode.Horizontal, mapper.Mirroring);
    }

    [Fact]
    public void Mirroring_InfersVerticalFromMatchingNametableBankPairs()
    {
        var mapper = new Mapper19(BuildCartridge(2, 8));
        mapper.CpuWrite(0xC000, 0xE0); // quadrant 0 -> page 0
        mapper.CpuWrite(0xC800, 0xE1); // quadrant 1 -> page 1
        mapper.CpuWrite(0xD000, 0xE0); // quadrant 2 -> page 0
        mapper.CpuWrite(0xD800, 0xE1); // quadrant 3 -> page 1

        Assert.Equal(MirroringMode.Vertical, mapper.Mirroring);
    }

    [Fact]
    public void SoundRam_AutoIncrement_AdvancesAddressOnEachAccess()
    {
        var mapper = new Mapper19(BuildCartridge(2, 8));
        mapper.CpuWrite(0xF800, 0x80); // address=0, auto-increment on
        mapper.CpuWrite(0x4800, 0x11);
        mapper.CpuWrite(0x4800, 0x22);

        mapper.CpuWrite(0xF800, 0x80); // reset address back to 0, keep auto-increment
        Assert.Equal(0x11, mapper.CpuRead(0x4800));
        Assert.Equal(0x22, mapper.CpuRead(0x4800));
    }

    [Fact]
    public void SoundRam_WithoutAutoIncrement_AddressStaysFixed()
    {
        var mapper = new Mapper19(BuildCartridge(2, 8));
        mapper.CpuWrite(0xF800, 0x05); // address=5, no auto-increment
        mapper.CpuWrite(0x4800, 0x99);

        Assert.Equal(0x99, mapper.CpuRead(0x4800));
        Assert.Equal(0x99, mapper.CpuRead(0x4800)); // still address 5
    }

    [Fact]
    public void Irq_CountsUpAndFiresAt0x7FFF_ThenFreezes()
    {
        var mapper = new Mapper19(BuildCartridge(2, 8));
        mapper.CpuWrite(0x5000, 0xFE);
        mapper.CpuWrite(0x5800, 0xFF); // high byte 0x7F (masked) + enable

        mapper.ClockCpu(); // 0x7FFE -> 0x7FFF
        Assert.True(mapper.IrqLine);

        mapper.ClockCpu(); // frozen at 0x7FFF, no crash, still pending
        Assert.True(mapper.IrqLine);
    }

    [Fact]
    public void Wavetable_ProducesVaryingNonZeroOutput_WhenConfigured()
    {
        var mapper = new Mapper19(BuildCartridge(prg8kBanks: 4, chr1kBanks: 8));
        WriteSoundRam(mapper, 0x00, 0xF0); // waveform nibbles at offsets 0-3: 0,15,0,15
        WriteSoundRam(mapper, 0x01, 0xF0);
        // Channel 7's register base is 0x40 + 7*8 = 0x78.
        WriteSoundRam(mapper, 0x78, 0x40); // channel 7 freqLo
        WriteSoundRam(mapper, 0x7A, 0x00); // freqMid
        WriteSoundRam(mapper, 0x7C, 0x00); // freqHi=0, waveform length code 0 -> length 4
        WriteSoundRam(mapper, 0x7E, 0x0F); // volume 15, channel-count code 0 -> 1 active channel
        WriteSoundRam(mapper, 0x7F, 0x00); // waveform address 0

        var samples = new HashSet<float>();
        for (int i = 0; i < 500; i++)
        {
            mapper.ClockCpu();
            samples.Add(mapper.GetAudioSample());
        }

        Assert.True(samples.Count > 1);
        Assert.Contains(samples, s => s != 0f);
    }

    [Fact]
    public void Wavetable_ZeroVolume_IsSilent()
    {
        var mapper = new Mapper19(BuildCartridge(prg8kBanks: 4, chr1kBanks: 8));
        WriteSoundRam(mapper, 0x00, 0xF0);
        WriteSoundRam(mapper, 0x78, 0x40);
        WriteSoundRam(mapper, 0x7C, 0x00);
        WriteSoundRam(mapper, 0x7E, 0x00); // volume 0
        WriteSoundRam(mapper, 0x7F, 0x00);

        for (int i = 0; i < 50; i++)
        {
            mapper.ClockCpu();
        }

        Assert.Equal(0f, mapper.GetAudioSample());
    }

    [Fact]
    public void ChrRam_PersistsWrites_WhenCartridgeHasNoChrRom()
    {
        var mapper = new Mapper19(BuildCartridge(prg8kBanks: 2, chr1kBanks: 0));
        mapper.PpuWrite(0x0011, 0x44);

        Assert.Equal(0x44, mapper.PpuRead(0x0011));
    }
}
