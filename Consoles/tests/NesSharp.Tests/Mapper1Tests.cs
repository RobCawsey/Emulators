using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Mapper1Tests
{
    /// <summary>Builds a synthetic MMC1 cartridge where every 16KB PRG bank is filled with
    /// its own bank index and every 4KB CHR bank with 0x80+its index, so reads reveal
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
        rom[6] = 0x10; // mapper low nibble = 1 (MMC1)
        rom[7] = 0x00;

        for (int bank = 0; bank < prgBanks16k; bank++)
        {
            int offset = 16 + bank * 16384;
            Array.Fill(rom, (byte)bank, offset, 16384);
        }
        int chrOffsetBase = 16 + prgSize;
        for (int bank = 0; bank < chrBanks8k * 2; bank++)
        {
            int offset = chrOffsetBase + bank * 4096;
            Array.Fill(rom, (byte)(0x80 + bank), offset, 4096);
        }

        return Cartridge.LoadFromInes(rom);
    }

    private static void WriteRegister(Mapper1 mapper, ushort address, byte fiveBitValue)
    {
        for (int i = 0; i < 5; i++)
        {
            mapper.CpuWrite(address, (byte)((fiveBitValue >> i) & 1));
        }
    }

    [Theory]
    [InlineData((byte)0x00, MirroringMode.SingleScreenLower)]
    [InlineData((byte)0x01, MirroringMode.SingleScreenUpper)]
    [InlineData((byte)0x02, MirroringMode.Vertical)]
    [InlineData((byte)0x03, MirroringMode.Horizontal)]
    public void ControlRegister_MirroringBitsSelectExpectedMode(byte mirroringBits, MirroringMode expected)
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1));
        WriteRegister(mapper, 0x8000, mirroringBits);

        Assert.Equal(expected, mapper.Mirroring);
    }

    [Fact]
    public void BitSevenWrite_ResetsShiftRegisterMidSequence()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1));

        mapper.CpuWrite(0x8000, 1); // partial write: 1 of 5 bits shifted in
        mapper.CpuWrite(0x8000, 1); // 2 of 5
        mapper.CpuWrite(0x8000, 0x80); // bit7 set: reset, and force PRG mode 3

        // A clean 5-write sequence right after should land correctly, proving the partial
        // shift was discarded rather than contributing leftover bits.
        WriteRegister(mapper, 0x8000, 0x03); // horizontal mirroring
        Assert.Equal(MirroringMode.Horizontal, mapper.Mirroring);
    }

    [Fact]
    public void BitSevenWrite_ForcesPrgMode3()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 4, chrBanks8k: 1));
        WriteRegister(mapper, 0x8000, 0x00); // mode 0 (32KB switch)
        mapper.CpuWrite(0x8000, 0x80); // reset -> forces mode 3

        // Mode 3 fixes the LAST bank (index 3) at $C000, regardless of the PRG bank register.
        Assert.Equal(3, mapper.CpuRead(0xC000));
    }

    [Fact]
    public void PrgMode0_Switches32KUnit_IgnoringBankNumberLowBit()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 4, chrBanks8k: 1));
        WriteRegister(mapper, 0x8000, 0x00); // control: PRG mode 0 (32KB), CHR mode 0
        WriteRegister(mapper, 0xE000, 0x03); // bank 3 -> low bit ignored -> pairs with bank 2

        Assert.Equal(2, mapper.CpuRead(0x8000)); // start of the 32KB window: bank 2
        Assert.Equal(3, mapper.CpuRead(0xC000)); // second half of the window: bank 3
    }

    [Fact]
    public void PrgMode2_FixesFirstBank_SwitchesSecond()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 4, chrBanks8k: 1));
        WriteRegister(mapper, 0x8000, 0x08); // control: PRG mode 2 (bits2-3 = 10)
        WriteRegister(mapper, 0xE000, 0x02); // select bank 2 for the switched half

        Assert.Equal(0, mapper.CpuRead(0x8000)); // fixed: always bank 0
        Assert.Equal(2, mapper.CpuRead(0xC000)); // switched: bank 2
    }

    [Fact]
    public void PrgMode3_SwitchesFirstBank_FixesLast()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 4, chrBanks8k: 1));
        WriteRegister(mapper, 0x8000, 0x0C); // control: PRG mode 3 (bits2-3 = 11), the power-on default
        WriteRegister(mapper, 0xE000, 0x01); // select bank 1 for the switched half

        Assert.Equal(1, mapper.CpuRead(0x8000)); // switched: bank 1
        Assert.Equal(3, mapper.CpuRead(0xC000)); // fixed: always the last bank (3)
    }

    [Fact]
    public void ChrMode0_8KBSwitch_IgnoresBankNumberLowBit()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 2)); // 4 4KB CHR banks: 0x80-0x83
        WriteRegister(mapper, 0x8000, 0x00); // CHR mode 0
        WriteRegister(mapper, 0xA000, 0x03); // low bit ignored -> pairs with bank 2

        Assert.Equal(0x82, mapper.PpuRead(0x0000)); // first half of the 8KB window: bank 2
        Assert.Equal(0x83, mapper.PpuRead(0x1000)); // second half: bank 3
    }

    [Fact]
    public void ChrMode1_TwoIndependentFourKBBanks()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 2));
        WriteRegister(mapper, 0x8000, 0x10); // CHR mode 1 (bit4 set)
        WriteRegister(mapper, 0xA000, 0x01); // CHR bank 0 -> bank 1
        WriteRegister(mapper, 0xC000, 0x03); // CHR bank 1 -> bank 3

        Assert.Equal(0x81, mapper.PpuRead(0x0000));
        Assert.Equal(0x83, mapper.PpuRead(0x1000));
    }

    [Fact]
    public void PrgRam_ReadWrite_WhenEnabled()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1));
        // PRG bank register bit4=0 means enabled — leave it at its post-construction default (0).
        mapper.CpuWrite(0x6123, 0x42);

        Assert.Equal(0x42, mapper.CpuRead(0x6123));
    }

    [Fact]
    public void PrgRam_Disabled_IgnoresWrites()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 1));
        WriteRegister(mapper, 0xE000, 0x10); // PRG bank register bit4=1: PRG-RAM disabled

        mapper.CpuWrite(0x6000, 0x42);

        Assert.Equal(0x00, mapper.CpuRead(0x6000));
    }

    [Fact]
    public void ChrRam_PersistsWrites_WhenCartridgeHasNoChrRom()
    {
        var mapper = new Mapper1(BuildCartridge(prgBanks16k: 2, chrBanks8k: 0)); // 0 CHR banks -> CHR-RAM
        mapper.PpuWrite(0x0042, 0x99);

        Assert.Equal(0x99, mapper.PpuRead(0x0042));
    }
}
