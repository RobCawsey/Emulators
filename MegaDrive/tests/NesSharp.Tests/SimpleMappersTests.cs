using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class SimpleMappersTests
{
    /// <summary>Builds a synthetic cartridge where every 16KB PRG bank is filled with its own
    /// bank index and every 8KB CHR bank with 0x80+its index.</summary>
    private static Cartridge BuildCartridge(int prgBanks16k, int chrBanks8k, byte mapperNumber, byte flags6 = 0x00)
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
        rom[6] = (byte)(flags6 | ((mapperNumber & 0x0F) << 4));
        rom[7] = (byte)(mapperNumber & 0xF0);

        for (int bank = 0; bank < prgBanks16k; bank++)
        {
            Array.Fill(rom, (byte)bank, 16 + bank * 16384, 16384);
        }
        int chrOffsetBase = 16 + prgSize;
        for (int bank = 0; bank < chrBanks8k; bank++)
        {
            Array.Fill(rom, (byte)(0x80 + bank), chrOffsetBase + bank * 8192, 8192);
        }
        return Cartridge.LoadFromInes(rom);
    }

    [Fact]
    public void UxRom_SwitchesFirstSlot_FixesLastSlotToFinalBank()
    {
        var mapper = new Mapper2(BuildCartridge(prgBanks16k: 4, chrBanks8k: 0, mapperNumber: 2));
        mapper.CpuWrite(0x8000, 1);

        Assert.Equal(1, mapper.CpuRead(0x8000)); // switched
        Assert.Equal(3, mapper.CpuRead(0xC000)); // fixed: last bank
    }

    [Fact]
    public void UxRom_ChrIsAlwaysWritableRam()
    {
        var mapper = new Mapper2(BuildCartridge(prgBanks16k: 2, chrBanks8k: 0, mapperNumber: 2));
        mapper.PpuWrite(0x0010, 0x42);

        Assert.Equal(0x42, mapper.PpuRead(0x0010));
    }

    [Fact]
    public void CnRom_PrgIsFixed_OnlyChrSwitches()
    {
        var mapper = new Mapper3(BuildCartridge(prgBanks16k: 1, chrBanks8k: 4, mapperNumber: 3));
        Assert.Equal(0, mapper.CpuRead(0x8000)); // only one PRG bank regardless of writes
        mapper.CpuWrite(0x8000, 99); // shouldn't affect PRG

        mapper.CpuWrite(0x8000, 2); // select CHR bank 2
        Assert.Equal(0x82, mapper.PpuRead(0x0000));
        Assert.Equal(0, mapper.CpuRead(0x8000));
    }

    [Fact]
    public void AxRom_Switches32KBWindow()
    {
        var mapper = new Mapper7(BuildCartridge(prgBanks16k: 4, chrBanks8k: 0, mapperNumber: 7)); // 2 32KB banks
        mapper.CpuWrite(0x8000, 0x01); // 32KB bank 1

        Assert.Equal(2, mapper.CpuRead(0x8000)); // bank1 starts at 16KB-bank index 2
        Assert.Equal(3, mapper.CpuRead(0xC000)); // second half of that 32KB window
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.SingleScreenLower)]
    [InlineData((byte)0x10, MirroringMode.SingleScreenUpper)]
    public void AxRom_ControlsSingleScreenMirroring(byte control, MirroringMode expected)
    {
        var mapper = new Mapper7(BuildCartridge(prgBanks16k: 2, chrBanks8k: 0, mapperNumber: 7));
        mapper.CpuWrite(0x8000, control);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void GxRom_SwitchesPrgAndChrIndependently()
    {
        var mapper = new Mapper66(BuildCartridge(prgBanks16k: 8, chrBanks8k: 4, mapperNumber: 66)); // 4x32KB PRG
        mapper.CpuWrite(0x8000, 0x21); // PRG bank 2 (bits4-5), CHR bank 1 (bits0-1)

        Assert.Equal(4, mapper.CpuRead(0x8000)); // PRG 32KB-bank2 starts at 16KB-bank index 4
        Assert.Equal(0x81, mapper.PpuRead(0x0000));
    }
}
