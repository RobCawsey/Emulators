using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class LatchMappersTests
{
    /// <summary>For Mapper9 (8KB PRG granularity): every 8KB PRG bank filled with its own
    /// index, every 4KB CHR bank filled with 0x80+its index.</summary>
    private static Cartridge BuildMapper9Cartridge(int prg8kBanks, int chr4kBanks)
    {
        int prgSize = prg8kBanks * 8192;
        int chrSize = chr4kBanks * 4096;
        var rom = new byte[16 + prgSize + chrSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = (byte)(prgSize / 16384);
        rom[5] = (byte)(chrSize / 8192);
        rom[6] = 0x90; // mapper low nibble = 9
        rom[7] = 0x00;

        for (int b = 0; b < prg8kBanks; b++)
        {
            Array.Fill(rom, (byte)b, 16 + b * 8192, 8192);
        }
        int chrBase = 16 + prgSize;
        for (int b = 0; b < chr4kBanks; b++)
        {
            Array.Fill(rom, (byte)(0x80 + b), chrBase + b * 4096, 4096);
        }
        return Cartridge.LoadFromInes(rom);
    }

    /// <summary>For Mapper10 (16KB PRG granularity): every 16KB PRG bank filled with its own
    /// index, every 4KB CHR bank filled with 0x80+its index.</summary>
    private static Cartridge BuildMapper10Cartridge(int prg16kBanks, int chr4kBanks)
    {
        int prgSize = prg16kBanks * 16384;
        int chrSize = chr4kBanks * 4096;
        var rom = new byte[16 + prgSize + chrSize];
        rom[0] = (byte)'N';
        rom[1] = (byte)'E';
        rom[2] = (byte)'S';
        rom[3] = 0x1A;
        rom[4] = (byte)prg16kBanks;
        rom[5] = (byte)(chrSize / 8192);
        rom[6] = 0xA0; // mapper low nibble = 10 (0xA)
        rom[7] = 0x00;

        for (int b = 0; b < prg16kBanks; b++)
        {
            Array.Fill(rom, (byte)b, 16 + b * 16384, 16384);
        }
        int chrBase = 16 + prgSize;
        for (int b = 0; b < chr4kBanks; b++)
        {
            Array.Fill(rom, (byte)(0x80 + b), chrBase + b * 4096, 4096);
        }
        return Cartridge.LoadFromInes(rom);
    }

    [Fact]
    public void Mapper9_ChrLatch_SwitchesOnFdFeTrigger_ForTheZeroHalf()
    {
        var mapper = new Mapper9(BuildMapper9Cartridge(prg8kBanks: 2, chr4kBanks: 8));
        mapper.CpuWrite(0xB000, 2); // FD bank
        mapper.CpuWrite(0xC000, 5); // FE bank

        Assert.Equal(0x82, mapper.PpuRead(0x0000)); // default latch state (FD)

        mapper.PpuRead(0x0FE8); // triggers latch -> FE
        Assert.Equal(0x85, mapper.PpuRead(0x0000));

        mapper.PpuRead(0x0FD8); // triggers latch -> FD again
        Assert.Equal(0x82, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper9_ChrLatch_IndependentForTheOneThousandHalf()
    {
        var mapper = new Mapper9(BuildMapper9Cartridge(prg8kBanks: 2, chr4kBanks: 8));
        mapper.CpuWrite(0xD000, 3); // FD bank for $1000
        mapper.CpuWrite(0xE000, 6); // FE bank for $1000
        mapper.CpuWrite(0xB000, 1); // FD bank for $0000 (independent register)

        Assert.Equal(0x83, mapper.PpuRead(0x1000)); // default FD
        Assert.Equal(0x81, mapper.PpuRead(0x0000)); // unaffected by the other half

        mapper.PpuRead(0x1FE8); // triggers latch1 -> FE
        Assert.Equal(0x86, mapper.PpuRead(0x1000));
        Assert.Equal(0x81, mapper.PpuRead(0x0000)); // latch0 untouched by latch1's trigger
    }

    [Fact]
    public void Mapper9_PrgBanking_SwitchesFirstWindow_FixesLastThreeToTopOfRom()
    {
        var mapper = new Mapper9(BuildMapper9Cartridge(prg8kBanks: 8, chr4kBanks: 2));
        mapper.CpuWrite(0xA000, 3); // switch $8000-$9FFF to 8KB bank 3

        Assert.Equal(3, mapper.CpuRead(0x8000));
        Assert.Equal(5, mapper.CpuRead(0xA000)); // fixed: bankCount-3
        Assert.Equal(6, mapper.CpuRead(0xC000)); // fixed: bankCount-2
        Assert.Equal(7, mapper.CpuRead(0xE000)); // fixed: bankCount-1
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.Vertical)]
    [InlineData((byte)0x01, MirroringMode.Horizontal)]
    public void Mapper9_MirroringRegister(byte value, MirroringMode expected)
    {
        var mapper = new Mapper9(BuildMapper9Cartridge(prg8kBanks: 2, chr4kBanks: 2));
        mapper.CpuWrite(0xF000, value);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void Mapper10_ChrLatch_SameMechanismAsMapper9()
    {
        var mapper = new Mapper10(BuildMapper10Cartridge(prg16kBanks: 2, chr4kBanks: 8));
        mapper.CpuWrite(0xB000, 2);
        mapper.CpuWrite(0xC000, 5);

        Assert.Equal(0x82, mapper.PpuRead(0x0000));
        mapper.PpuRead(0x0FE8);
        Assert.Equal(0x85, mapper.PpuRead(0x0000));
    }

    [Fact]
    public void Mapper10_PrgBanking_Switches16KBWindow_FixesLastBank()
    {
        var mapper = new Mapper10(BuildMapper10Cartridge(prg16kBanks: 4, chr4kBanks: 2));
        mapper.CpuWrite(0xA000, 1); // switch $8000-$BFFF to 16KB bank 1

        Assert.Equal(1, mapper.CpuRead(0x8000));
        Assert.Equal(3, mapper.CpuRead(0xC000)); // fixed: last bank
    }

    [Fact]
    public void Mapper10_PrgRam_AlwaysEnabled()
    {
        var mapper = new Mapper10(BuildMapper10Cartridge(prg16kBanks: 2, chr4kBanks: 2));
        mapper.CpuWrite(0x6055, 0x42);

        Assert.Equal(0x42, mapper.CpuRead(0x6055));
    }
}
