using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Vrc7FmOperatorTests
{
    [Fact]
    public void KeyOn_EnvelopeRampsUp_ProducingLouderOutputOverTime()
    {
        var op = new Vrc7FmOperator { Multiplier = 1, AttackRate = 1, DecayRate = 0, SustainLevel = 15, ReleaseRate = 0, TotalLevel = 0 };
        op.KeyOn();
        op.AdvancePhase(1000, 44100); // move off phase 0 so sin(phase) is nonzero

        float early = Math.Abs(op.Output(0));
        for (int i = 0; i < 50; i++)
        {
            op.ClockEnvelope();
        }
        float later = Math.Abs(op.Output(0));

        Assert.True(later > early);
    }

    [Fact]
    public void KeyOff_ReleasesEnvelopeToSilence()
    {
        var op = new Vrc7FmOperator { Multiplier = 1, AttackRate = 15, DecayRate = 15, SustainLevel = 15, ReleaseRate = 15, TotalLevel = 0 };
        op.KeyOn();
        op.AdvancePhase(1000, 44100);
        for (int i = 0; i < 10; i++)
        {
            op.ClockEnvelope();
        }

        op.KeyOff();
        for (int i = 0; i < 200; i++)
        {
            op.ClockEnvelope();
        }

        Assert.Equal(0f, op.Output(0));
    }

    [Fact]
    public void MaxTotalLevel_AttenuatesFullyToSilence()
    {
        var op = new Vrc7FmOperator { Multiplier = 1, AttackRate = 15, TotalLevel = 63 };
        op.KeyOn();
        op.AdvancePhase(1000, 44100);
        for (int i = 0; i < 50; i++)
        {
            op.ClockEnvelope();
        }

        Assert.Equal(0f, op.Output(0));
    }
}

public class Vrc7FmChannelTests
{
    [Fact]
    public void KeyOnWithVolume_EventuallyProducesAudibleOutput()
    {
        var channel = new Vrc7FmChannel { FNumber = 200, Block = 2, Volume = 15 };
        channel.Modulator.AttackRate = 15;
        channel.Modulator.SustainLevel = 15;
        channel.Modulator.TotalLevel = 10;
        channel.Carrier.AttackRate = 15;
        channel.Carrier.SustainLevel = 15;
        channel.Carrier.TotalLevel = 0;

        channel.SetKeyOn(true);

        bool heard = false;
        for (int i = 0; i < 2000; i++)
        {
            channel.Clock(44100);
            if (Math.Abs(channel.Output()) > 0.001f)
            {
                heard = true;
                break;
            }
        }

        Assert.True(heard);
    }

    [Fact]
    public void ZeroVolume_IsSilentRegardlessOfEnvelope()
    {
        var channel = new Vrc7FmChannel { FNumber = 200, Block = 2, Volume = 0 };
        channel.Modulator.AttackRate = 15;
        channel.Modulator.TotalLevel = 10;
        channel.Carrier.AttackRate = 15;
        channel.Carrier.SustainLevel = 15;
        channel.SetKeyOn(true);

        for (int i = 0; i < 500; i++)
        {
            channel.Clock(44100);
        }

        Assert.Equal(0f, channel.Output());
    }
}

public class Mapper85Tests
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
        const int mapperNumber = 85;
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

    private static void WriteAudio(Mapper85 mapper, byte register, byte value)
    {
        mapper.CpuWrite(0x9010, register);
        mapper.CpuWrite(0x9030, value);
    }

    [Fact]
    public void PrgBanking_ThreeSwitchableSlots_FixedLastBank()
    {
        var mapper = new Mapper85(BuildCartridge(prg8kBanks: 8, chr1kBanks: 8));
        mapper.CpuWrite(0x8000, 2);
        mapper.CpuWrite(0x8010, 4);
        mapper.CpuWrite(0x9000, 6);

        Assert.Equal(2, mapper.CpuRead(0x8000));
        Assert.Equal(4, mapper.CpuRead(0xA000));
        Assert.Equal(6, mapper.CpuRead(0xC000));
        Assert.Equal(7, mapper.CpuRead(0xE000));
    }

    [Fact]
    public void ChrBanking_EightIndependentOneKBBanks()
    {
        var mapper = new Mapper85(BuildCartridge(prg8kBanks: 2, chr1kBanks: 16));
        mapper.CpuWrite(0xA000, 3); // CHR bank 0
        mapper.CpuWrite(0xD010, 9); // CHR bank 7

        Assert.Equal(0x83, mapper.PpuRead(0x0000));
        Assert.Equal(0x89, mapper.PpuRead(0x1C00));
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.Vertical)]
    [InlineData((byte)0x01, MirroringMode.Horizontal)]
    public void MirroringRegister_SelectsExpectedMode(byte value, MirroringMode expected)
    {
        var mapper = new Mapper85(BuildCartridge(2, 8));
        mapper.CpuWrite(0xE000, value);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void WramEnableBit_GatesSixThousandRegion()
    {
        var mapper = new Mapper85(BuildCartridge(2, 8));
        mapper.CpuWrite(0x6000, 0x42); // disabled by default -> ignored

        Assert.Equal(0x00, mapper.CpuRead(0x6000));

        mapper.CpuWrite(0xE000, 0x80); // enable WRAM
        mapper.CpuWrite(0x6000, 0x42);
        Assert.Equal(0x42, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void SoundDisableBit_SilencesOutputEvenWithActiveChannels()
    {
        var mapper = new Mapper85(BuildCartridge(2, 8));
        WriteAudio(mapper, 0x10, 0x80); // ch0 freq lo
        WriteAudio(mapper, 0x20, 0x90); // ch0 key on, block=2
        WriteAudio(mapper, 0x30, 0xF1); // ch0 volume 15, patch 1

        for (int i = 0; i < 500; i++)
        {
            mapper.ClockCpu();
        }

        mapper.CpuWrite(0xE000, 0x40); // sound disable bit
        Assert.Equal(0f, mapper.GetAudioSample());
    }

    [Fact]
    public void KeyOnWithPresetPatch_EventuallyProducesAudibleOutput()
    {
        var mapper = new Mapper85(BuildCartridge(2, 8));
        WriteAudio(mapper, 0x10, 0x80); // ch0 freq lo
        WriteAudio(mapper, 0x20, 0x90); // ch0 key on, block=2
        WriteAudio(mapper, 0x30, 0xF1); // ch0 volume 15, patch 1

        bool heard = false;
        for (int i = 0; i < 3000; i++)
        {
            mapper.ClockCpu();
            if (Math.Abs(mapper.GetAudioSample()) > 0.0001f)
            {
                heard = true;
                break;
            }
        }

        Assert.True(heard);
    }

    [Fact]
    public void AllPatchIndices_CanBeSelectedWithoutError()
    {
        var mapper = new Mapper85(BuildCartridge(2, 8));
        for (int p = 0; p < 16; p++)
        {
            WriteAudio(mapper, 0x30, (byte)(0xF0 | p));
        }
        // Reaching here without an exception is the assertion.
    }

    [Fact]
    public void CustomPatchRegisters_LiveUpdateChannelsCurrentlyUsingPatchZero()
    {
        var mapper = new Mapper85(BuildCartridge(2, 8));
        WriteAudio(mapper, 0x10, 0x80);
        WriteAudio(mapper, 0x20, 0x90);
        WriteAudio(mapper, 0x30, 0xF0); // patch 0 (custom), volume 15

        WriteAudio(mapper, 0x00, 0x01); // custom modulator multiplier=1
        WriteAudio(mapper, 0x01, 0x01); // custom carrier multiplier=1
        WriteAudio(mapper, 0x04, 0xF0); // custom modulator attack=15, decay=0
        WriteAudio(mapper, 0x05, 0xF0); // custom carrier attack=15, decay=0
        WriteAudio(mapper, 0x06, 0xF0); // custom modulator sustain=15, release=0
        WriteAudio(mapper, 0x07, 0xF0); // custom carrier sustain=15, release=0

        bool heard = false;
        for (int i = 0; i < 3000; i++)
        {
            mapper.ClockCpu();
            if (Math.Abs(mapper.GetAudioSample()) > 0.0001f)
            {
                heard = true;
                break;
            }
        }

        Assert.True(heard);
    }

    [Fact]
    public void ChrRam_PersistsWrites_WhenCartridgeHasNoChrRom()
    {
        var mapper = new Mapper85(BuildCartridge(prg8kBanks: 2, chr1kBanks: 0));
        mapper.PpuWrite(0x0077, 0x88);

        Assert.Equal(0x88, mapper.PpuRead(0x0077));
    }
}
