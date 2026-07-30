using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Vrc6ChannelTests
{
    [Fact]
    public void Pulse_DutyProducesBothOnAndOffLevels()
    {
        var pulse = new Vrc6PulseChannel();
        pulse.WriteControl(0x0F); // duty=0 (1/16), volume=15, not digitized
        pulse.WriteFrequencyLow(0x01);
        pulse.WriteFrequencyHigh(0x80); // enabled, period=1

        var outputs = new List<byte>();
        for (int i = 0; i < 32; i++)
        {
            pulse.ClockTimer();
            outputs.Add(pulse.Output);
        }

        Assert.Contains((byte)15, outputs);
        Assert.Contains((byte)0, outputs);
    }

    [Fact]
    public void Pulse_DigitizedMode_AlwaysOutputsVolumeRegardlessOfDuty()
    {
        var pulse = new Vrc6PulseChannel();
        pulse.WriteControl(0x8F); // digitized, volume=15, duty irrelevant
        pulse.WriteFrequencyLow(0x01);
        pulse.WriteFrequencyHigh(0x80);

        for (int i = 0; i < 20; i++)
        {
            pulse.ClockTimer();
            Assert.Equal(15, pulse.Output);
        }
    }

    [Fact]
    public void Pulse_Disabled_OutputsZero()
    {
        var pulse = new Vrc6PulseChannel();
        pulse.WriteControl(0x0F);
        pulse.WriteFrequencyHigh(0x00); // enable bit clear

        pulse.ClockTimer();
        Assert.Equal(0, pulse.Output);
    }

    [Fact]
    public void Sawtooth_AccumulatesThenPeriodicallyResets()
    {
        var saw = new Vrc6SawtoothChannel();
        saw.WriteControl(0x10);
        saw.WriteFrequencyLow(0x00);
        saw.WriteFrequencyHigh(0x80); // enabled, period=0 (fastest)

        var outputs = new List<byte>();
        for (int i = 0; i < 40; i++)
        {
            saw.ClockTimer();
            outputs.Add(saw.Output);
        }

        Assert.Contains((byte)0, outputs); // resets
        Assert.True(outputs.Max() > 0); // and rises
    }

    [Fact]
    public void Sawtooth_Disabled_OutputsZero()
    {
        var saw = new Vrc6SawtoothChannel();
        saw.WriteControl(0x3F);
        saw.WriteFrequencyHigh(0x00);

        saw.ClockTimer();
        Assert.Equal(0, saw.Output);
    }
}

public class Mapper24Tests
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
        const int mapperNumber = 24;
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

    [Fact]
    public void PrgBanking_16KAt8000_8KAtC000_FixedLastAtE000()
    {
        var mapper = new Mapper24(BuildCartridge(prg8kBanks: 8, chr1kBanks: 16)); // 64KB PRG
        mapper.CpuWrite(0x8000, 1); // 16KB bank 1 -> 8KB banks 2,3
        mapper.CpuWrite(0xC000, 4); // 8KB bank 4

        Assert.Equal(2, mapper.CpuRead(0x8000));
        Assert.Equal(4, mapper.CpuRead(0xC000));
        Assert.Equal(7, mapper.CpuRead(0xE000)); // fixed: last bank
    }

    [Fact]
    public void Chr_EightIndependentOneKBBanks()
    {
        var mapper = new Mapper24(BuildCartridge(prg8kBanks: 2, chr1kBanks: 16));
        mapper.CpuWrite(0xD000, 3); // CHR bank 0 (address&3==0)
        mapper.CpuWrite(0xE003, 9); // CHR bank 7 (4 + (address&3==3))

        Assert.Equal(0x83, mapper.PpuRead(0x0000));
        Assert.Equal(0x89, mapper.PpuRead(0x1C00));
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.Vertical)]
    [InlineData((byte)0x04, MirroringMode.Horizontal)]
    [InlineData((byte)0x08, MirroringMode.SingleScreenLower)]
    [InlineData((byte)0x0C, MirroringMode.SingleScreenUpper)]
    public void MirroringRegister_SelectsExpectedMode(byte value, MirroringMode expected)
    {
        var mapper = new Mapper24(BuildCartridge(2, 16));
        mapper.CpuWrite(0xB003, value);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void PrgRam_ReadWrite()
    {
        var mapper = new Mapper24(BuildCartridge(2, 16));
        mapper.CpuWrite(0x6042, 0x77);

        Assert.Equal(0x77, mapper.CpuRead(0x6042));
    }

    [Fact]
    public void Irq_CycleMode_FiresOnOverflowPastFF()
    {
        var mapper = new Mapper24(BuildCartridge(2, 16));
        mapper.CpuWrite(0xF000, 0xFD); // latch = 253
        mapper.CpuWrite(0xF001, 0x06); // enable + cycle mode -> reloads counter to 253

        mapper.ClockCpu(); // 253 -> 254
        Assert.False(mapper.IrqLine);
        mapper.ClockCpu(); // 254 -> 255
        Assert.False(mapper.IrqLine);
        mapper.ClockCpu(); // 255 overflows -> reload + pending
        Assert.True(mapper.IrqLine);
    }

    [Fact]
    public void Irq_ScanlineMode_ClocksTheCounterOncePer114CpuCycles()
    {
        var mapper = new Mapper24(BuildCartridge(2, 16));
        mapper.CpuWrite(0xF000, 0xFF); // latch = 255: the first counter-clock overflows immediately
        mapper.CpuWrite(0xF001, 0x02); // enable, scanline mode (bit2 clear)

        for (int i = 0; i < 113; i++)
        {
            mapper.ClockCpu();
            Assert.False(mapper.IrqLine);
        }
        mapper.ClockCpu(); // 114th cycle: prescaler crosses zero, counter clocks, overflows
        Assert.True(mapper.IrqLine);
    }

    [Fact]
    public void Irq_Acknowledge_ClearsPendingWithoutDisabling()
    {
        var mapper = new Mapper24(BuildCartridge(2, 16));
        mapper.CpuWrite(0xF000, 0xFF);
        mapper.CpuWrite(0xF001, 0x06); // enable + cycle mode
        mapper.ClockCpu();
        Assert.True(mapper.IrqLine);

        mapper.CpuWrite(0xF002, 0x00); // acknowledge
        Assert.False(mapper.IrqLine);

        mapper.ClockCpu(); // counter was already reloaded to 0xFF, so this overflows again
        Assert.True(mapper.IrqLine);
    }
}
