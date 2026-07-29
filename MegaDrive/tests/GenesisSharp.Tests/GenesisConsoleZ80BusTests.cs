using GenesisSharp.Core;
using GenesisSharp.Cpu68000;
using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

public class GenesisConsoleZ80BusTests
{
    [Fact]
    public void Ym2612Ports_RouteWritesToTheChipsTwoIndependentBanks()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (CpuZ80.IBus)console;

        bus.WriteByte(0x4000, 0x28); // Part I address latch
        bus.WriteByte(0x4001, 0xF0); // Part I data
        bus.WriteByte(0x4002, 0x30); // Part II address latch
        bus.WriteByte(0x4003, 0x11); // Part II data

        Assert.Equal(0xF0, console.Ym2612.ReadRegisterPart1(0x28));
        Assert.Equal(0x11, console.Ym2612.ReadRegisterPart2(0x30));
    }

    [Fact]
    public void Ym2612Ports_MirrorAcrossTheWholeBlock()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (CpuZ80.IBus)console;

        // Real hardware only decodes the low 2 bits across 0x4000-0x5FFF; 0x5000/0x5001
        // should reach the exact same Part I address/data ports as 0x4000/0x4001.
        bus.WriteByte(0x5000, 0x28);
        bus.WriteByte(0x5001, 0x99);

        Assert.Equal(0x99, console.Ym2612.ReadRegisterPart1(0x28));
    }

    [Fact]
    public void Ym2612Read_AlwaysReturnsTheStatusByte()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (CpuZ80.IBus)console;

        Assert.Equal(console.Ym2612.ReadStatus(), bus.ReadByte(0x4000));
    }

    [Fact]
    public void PsgPort_RoutesWritesToTheChip()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (CpuZ80.IBus)console;

        bus.WriteByte(0x7F11, 0x9A); // register 1 (channel 0 volume), data 0xA

        Assert.Equal(0xA, console.Psg.Volume[0]);
    }

    [Fact]
    public void PsgWrite_FromThe68000Side_ReachesTheSameChip()
    {
        // The PSG is wired to both buses on real hardware -- the 68000 can hit it directly
        // through the VDP's port block without going through the Z80 at all.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(0xC00011, 0x9A);

        Assert.Equal(0xA, console.Psg.Volume[0]);
    }

    [Fact]
    public void BankRegister_ShiftsInLsbFirstAcrossNineWrites()
    {
        // Register value 510 (0b1_1111_1110) times the 32KB page size lands exactly on
        // 0xFF0000 -- work RAM, which is convenient to verify since it's both readable and
        // writable through the 68000 bus. Bit 0 (the first bit shifted in) is 0; bits 1-8
        // (the next eight) are all 1.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var z80Bus = (CpuZ80.IBus)console;

        z80Bus.WriteByte(0x6000, 0x00);
        for (int i = 0; i < 8; i++)
        {
            z80Bus.WriteByte(0x6000, 0x01);
        }

        // Round-trip through the bank window: a Z80-side write at 0x8000 (window offset 0)
        // should land at 68000 address 0xFF0000.
        z80Bus.WriteByte(0x8000, 0x42);
        var cpuBus = (Cpu68000.IBus)console;
        Assert.Equal(0x42, cpuBus.ReadByte(0xFF0000));

        // And the reverse direction: a write through the normal 68000 work-RAM path should be
        // visible back through the Z80's window.
        cpuBus.WriteByte(0xFF0005, 0x77);
        Assert.Equal(0x77, z80Bus.ReadByte(0x8005));
    }

    [Fact]
    public void BankWindow_CanReadCartridgeRomThroughTheZ80Side()
    {
        var rom = new byte[0x10000];
        rom[0x8000] = 0x55;
        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        var z80Bus = (CpuZ80.IBus)console;

        // Bank register = 1 (only bit 0, the first bit written, is set) -> window base 0x8000.
        z80Bus.WriteByte(0x6000, 0x01);
        for (int i = 0; i < 8; i++)
        {
            z80Bus.WriteByte(0x6000, 0x00);
        }

        Assert.Equal(0x55, z80Bus.ReadByte(0x8000));
    }
}
