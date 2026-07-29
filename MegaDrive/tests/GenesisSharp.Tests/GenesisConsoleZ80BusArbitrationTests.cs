using GenesisSharp.Core;
using GenesisSharp.Cpu68000;
using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

public class GenesisConsoleZ80BusArbitrationTests
{
    /// <summary>ROM filled with 68000 NOPs (0x4E71), every fetch address even so the byte
    /// parity of the alternating 0x4E/0x71 pattern is never disturbed — see the identical
    /// helper in GenesisConsoleTests for why an all-zero ROM isn't safe to run a full
    /// RunFrame() against.</summary>
    private static Cartridge CreateNopRom()
    {
        var rom = new byte[0x10000];
        for (int i = 0; i < rom.Length; i += 2)
        {
            rom[i] = 0x4E;
            rom[i + 1] = 0x71;
        }

        rom[4] = 0x00; rom[5] = 0x00; rom[6] = 0x01; rom[7] = 0x00; // PC = 0x00000100

        return Cartridge.LoadFromBin(rom);
    }

    [Fact]
    public void BusRequestRegister_ReadsBusyBeforeRequestAndGrantedAfter()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        // Only the even address (0xA11100) actually carries the busreq status bit -- real
        // hardware's odd sub-address (0xA11101) is plain open bus, so these checks read the
        // even byte directly (matching the disassembled idiom real ROMs actually use, e.g.
        // "TST.B $00A11100"). With an all-zero test ROM and no Reset(), Cpu.PC's prefetched
        // byte is 0x00, so the only bit that varies is bit 0 (the busreq status itself).
        Assert.Equal(0x01, bus.ReadByte(0xA11100)); // not requested -> Z80 running -> "busy"

        bus.WriteWord(0xA11100, 0x0100); // the classic request idiom

        // No arbitration delay is modeled -- the grant is immediate, so the classic
        // "request then poll" loop (btst #0,($A11100); bne ...) falls through right away.
        Assert.Equal(0x00, bus.ReadByte(0xA11100));
        Assert.Equal(0x0000, bus.ReadWord(0xA11100));
    }

    [Fact]
    public void BusRequestRegister_ReleaseGoesBackToBusy()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        bus.WriteWord(0xA11100, 0x0100);
        bus.WriteWord(0xA11100, 0x0000);

        Assert.Equal(0x01, bus.ReadByte(0xA11100));
    }

    [Fact]
    public void ResetRegister_AssertingItClearsTheSoundCpusRegisters()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        console.SoundCpu.A = 0x42;
        console.SoundCpu.PC = 0x1234;
        var bus = (Cpu68000.IBus)console;

        bus.WriteWord(0xA11200, 0x0000); // 0 = hold in reset

        Assert.Equal(0, console.SoundCpu.A);
        Assert.Equal(0, console.SoundCpu.PC);
    }

    [Fact]
    public void ResetRegister_ReadBackReflectsHeldState()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        Assert.Equal(0x01, bus.ReadByte(0xA11201)); // not held -> running

        bus.WriteWord(0xA11200, 0x0000);
        Assert.Equal(0x00, bus.ReadByte(0xA11201));

        bus.WriteWord(0xA11200, 0x0100); // release
        Assert.Equal(0x01, bus.ReadByte(0xA11201));
    }

    [Fact]
    public void Z80Window_IsOpenBusUntilTheBusIsRequested()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(0xA00000, 0x42); // no effect -- bus not requested
        Assert.Equal(0xFF, bus.ReadByte(0xA00000));

        var z80Bus = (CpuZ80.IBus)console;
        Assert.Equal(0, z80Bus.ReadByte(0)); // the write above never reached Z80 RAM
    }

    [Fact]
    public void Z80Window_RoutesThroughToZ80RamOnceRequested()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;
        bus.WriteWord(0xA11100, 0x0100); // request the bus

        bus.WriteByte(0xA00010, 0x99);

        var z80Bus = (CpuZ80.IBus)console;
        Assert.Equal(0x99, z80Bus.ReadByte(0x0010));
        Assert.Equal(0x99, bus.ReadByte(0xA00010));
    }

    [Fact]
    public void Z80Window_ReachesTheYm2612DirectlyFromThe68000Side()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;
        bus.WriteWord(0xA11100, 0x0100); // request the bus

        bus.WriteByte(0xA04000, 0x28); // Part I address latch
        bus.WriteByte(0xA04001, 0xF0); // Part I data

        Assert.Equal(0xF0, console.Ym2612.ReadRegisterPart1(0x28));
    }

    [Fact]
    public void RunFrame_FreezesTheZ80WhileTheBusIsRequested()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();
        var bus = (Cpu68000.IBus)console;
        bus.WriteWord(0xA11100, 0x0100); // request the bus before running any frames

        console.RunFrame();

        Assert.Equal(0, console.SoundCpu.TotalCycles);
    }

    [Fact]
    public void RunFrame_ResumesTheZ80OnceReleased()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();
        var bus = (Cpu68000.IBus)console;
        bus.WriteWord(0xA11100, 0x0100);
        bus.WriteWord(0xA11100, 0x0000); // release again before running

        console.RunFrame();

        Assert.Equal(Vdp.LinesPerFrame * GenesisConsole.CyclesPerScanlineZ80, console.SoundCpu.TotalCycles);
    }

    [Fact]
    public void BankWindowSelfLoop_ReadsOpenBusInsteadOfRecursingForever()
    {
        // Bank register 321 -> window base 0xA08000, whose own low 16 bits (0x8000) point
        // right back into the Z80's own bank window -- a pathological loop a real program
        // would never construct on purpose, but a malformed one legally could. This must
        // terminate rather than blow the stack.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;
        bus.WriteWord(0xA11100, 0x0100); // request the bus

        var z80Bus = (CpuZ80.IBus)console;
        int register = 321;
        for (int i = 0; i < 9; i++)
        {
            z80Bus.WriteByte(0x6000, (byte)(register & 0x01));
            register >>= 1;
        }

        byte result = z80Bus.ReadByte(0x8000);

        Assert.Equal(0xFF, result);
    }
}
