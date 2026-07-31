using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class GenesisConsoleTests
{
    [Fact]
    public void Construction_WiresUpBothCpus()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));

        Assert.NotNull(console.Cpu);
        Assert.NotNull(console.SoundCpu);
    }

    [Fact]
    public void Reset_SucceedsNowThatRomAndWorkRamAreWiredUp()
    {
        // ROM and work RAM (and VDP ports) are now routed on the 68000 bus, so the CPU's
        // Reset() can read the vector table without hitting the memory-map stub.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));

        console.Reset();

        Assert.Equal(0u, console.Cpu.PC); // an all-zero ROM's vector table
    }

    [Fact]
    public void UnmappedAddresses_AreOpenBus_NotThrown()
    {
        // Found via real-ROM testing, twice: Scorpion Illuminati writes to $A12006 (not a
        // register this emulator or real hardware assigns any meaning to) during early boot;
        // and Omega Blast's Z80 sound driver legitimately bank-switches its 68000-side window
        // into $815337, well past this cartridge's actual ROM size, while otherwise running
        // correctly. Real hardware's address decode doesn't raise a bus error for either —
        // writes are ignored and reads float to open bus.
        var rom = new byte[0x10000];
        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(0xA12006, 0x42); // must not throw
        bus.WriteWord(0x815337, 0x04F6); // must not throw

        Assert.Equal(0xFF, bus.ReadByte(0xA12006));
        Assert.Equal(0xFFFF, bus.ReadWord(0x815337));
    }

    [Fact]
    public void Z80InOutPorts_AreOpenBus_NotThrown()
    {
        // Found via real-ROM testing: Omega Blast's Z80 sound driver executes a genuine OUT
        // instruction (whether from legitimate driver logic this core doesn't otherwise model,
        // or from the Z80 having wandered off into non-code memory and decoded a stray IN/OUT
        // byte). Real Z80 IN/OUT ports aren't wired to anything on the Genesis — every chip the
        // Z80 talks to is memory-mapped instead — so real hardware doesn't fault on either:
        // writes go nowhere, reads float to open bus.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (CpuZ80.IBus)console;

        bus.WritePort(0x11, 0x42); // must not throw

        Assert.Equal(0xFF, bus.ReadPort(0x11));
    }

    [Fact]
    public void WritesToRomSpace_AreSilentlyDropped_NotThrown()
    {
        // Found via real homebrew ROM testing: a vblank interrupt firing while the stack
        // pointer is uninitialized (or otherwise garbage) commonly pushes PC/SR right into
        // ROM space. Real hardware has no write line wired to the cartridge, so it just
        // does nothing -- it doesn't crash, and neither should this.
        var rom = new byte[0x10000];
        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(0x001000, 0x42);
        bus.WriteWord(0x007FFE, 0x1234);
        bus.WriteLong(0x3FFFF0, 0xDEADBEEF); // both words of the long stay under 0x400000

        Assert.Equal(0, rom[0x1000]); // the underlying ROM bytes are genuinely untouched
        Assert.Equal(rom[0], bus.ReadByte(0)); // and reads elsewhere are unaffected
    }

    [Fact]
    public void RequestVerticalBlankInterrupt_RaisesBothCpusInterrupts()
    {
        // Mirrors real Genesis wiring: the VDP's single vblank pulse feeds both the 68000's
        // level-6 IPL request and the Z80's INT line. Level 6, not 4 — found via real-ROM
        // testing (Crazy Driver) to be genuinely distinct from RequestHorizontalInterrupt's
        // level 4, not a shared line as originally (incorrectly) assumed.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));

        console.RequestVerticalBlankInterrupt();

        Assert.Equal(6, console.Cpu.PendingInterruptLevel);
        Assert.True(console.SoundCpu.InterruptPending);
    }

    [Fact]
    public void RequestHorizontalInterrupt_RaisesADifferentLevelThanVerticalBlank()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));

        console.RequestHorizontalInterrupt();

        Assert.Equal(4, console.Cpu.PendingInterruptLevel);
    }

    [Fact]
    public void VerticalBlankStarted_FromVdp_DeliversInterruptThroughConsole()
    {
        // End-to-end version of the delegation test above: the VDP's own event, not a direct
        // call, is what triggers it — this is what a future frame-driving loop will rely on.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Vdp.Registers[1] = 0x20; // enable vertical interrupts

        for (int line = 0; line < Vdp.ScreenHeight; line++)
        {
            console.Vdp.AdvanceScanline();
        }

        Assert.Equal(6, console.Cpu.PendingInterruptLevel);
        Assert.True(console.SoundCpu.InterruptPending);
    }

    [Fact]
    public void VdpControlPort_RoutedThroughThe68000Bus_ReachesTheVdp()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();

        // Register write via the control port: top bits "10", register 1, data 0x40 (display enable).
        ((Cpu68000.IBus)console).WriteWord(0xC00004, 0x8140);

        Assert.True(console.Vdp.DisplayEnabled);
    }

    [Fact]
    public void HvCounterPort_RoutedThroughThe68000Bus_ReachesTheVdp()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        for (int i = 0; i < 10; i++) console.Vdp.AdvanceScanline(); // V = 10
        console.Vdp.SetScanlineProgress(0.5); // H32 default, dot 171 -> HC 0x55 (85)

        ushort value = ((Cpu68000.IBus)console).ReadWord(0xC00008);

        Assert.Equal(console.Vdp.ReadHvCounter(), value);
        Assert.Equal((ushort)((10 << 8) | 85), value);
    }

    [Fact]
    public void HorizontalInterruptRequested_FromVdp_DeliversInterruptThroughConsole()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Vdp.Registers[0] = 0x10; // enable horizontal interrupts
        console.Vdp.Registers[10] = 0;   // fire every active-line tick

        console.Vdp.AdvanceScanline();

        Assert.Equal(4, console.Cpu.PendingInterruptLevel);
        // Unlike vblank, H-interrupts are a 68000-only concept -- they don't reach the Z80.
        Assert.False(console.SoundCpu.InterruptPending);
    }

    /// <summary>ROM filled with 68000 NOPs (0x4E71), every fetch address even so the byte
    /// parity of the alternating 0x4E/0x71 pattern is never disturbed. The vector table's PC
    /// entry is overwritten to point at an even offset inside that sea of NOPs; the SP entry
    /// is left untouched (unused — this program never touches the stack).</summary>
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
    public void RunFrame_AdvancesBothCpusByTheirExactPerFrameCycleBudget()
    {
        // Z80 RAM defaults to all zero, and 0x00 is itself a Z80 NOP, so the sound CPU also
        // runs a pure NOP stream. Both budgets (488 and 228) divide evenly by 4 (each CPU's
        // NOP cost), and the reset SR's default interrupt mask (7) blocks the vblank IRQ from
        // ever being serviced, so nothing perturbs the count — the totals should be exact.
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();

        console.RunFrame();

        Assert.Equal(Vdp.LinesPerFrame * GenesisConsole.CyclesPerScanlineM68000, console.Cpu.TotalCycles);
        Assert.Equal(Vdp.LinesPerFrame * GenesisConsole.CyclesPerScanlineZ80, console.SoundCpu.TotalCycles);
    }

    [Fact]
    public void RunFrame_CompletesExactlyOneFullFieldOfScanlines()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();

        console.RunFrame();

        Assert.Equal(0, console.Vdp.CurrentScanline); // wrapped back to the start of the next frame
    }

    [Fact]
    public void RunFrame_FiresTheVblankInterruptEvenWhenTheCpuLeavesItMasked()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset(); // default SR mask (7) blocks level 6 from being serviced
        console.Vdp.Registers[1] = 0x20; // enable vertical interrupts

        console.RunFrame();

        Assert.Equal(6, console.Cpu.PendingInterruptLevel); // requested, but never serviced
        Assert.True(console.SoundCpu.InterruptPending);
    }

    [Fact]
    public void RunFrame_ServicesTheVblankInterruptOnceTheCpuUnmasksIt()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();
        console.Vdp.Registers[1] = 0x20; // enable vertical interrupts
        console.Cpu.SR &= unchecked((ushort)~0x0700); // lower the interrupt priority mask to 0

        console.RunFrame();

        Assert.Equal(0, console.Cpu.PendingInterruptLevel); // serviced, not left pending
        Assert.True(console.Cpu.TotalCycles > Vdp.LinesPerFrame * GenesisConsole.CyclesPerScanlineM68000); // ack overhead
    }

    private static ushort RegisterWriteWord(int register, byte data) => (ushort)(0x8000 | (register << 8) | data);

    [Fact]
    public void VdpDma_SourceAddressWithStrippedBit23_AliasesToWorkRam()
    {
        // Found via real-ROM testing (Omega Blast's "30th ANNIVERSARY" splash): the VDP's DMA
        // source registers are hardware-limited to 23 bits (max 0x7FFFFE), so they can't encode
        // bit 23 of a work-RAM address ($FF0000-$FFFFFF). SGDK (and presumably every other real
        // toolchain) programs a DMA-from-RAM source by stripping that bit -- $FF8BAC becomes
        // $7F8BAC -- and real hardware's RAM chip-select still recognizes the stripped pattern as
        // RAM. Before this fix, $7F0000-$7FFFFF read as constant open bus (0xFF) instead, so every
        // DMA-from-RAM transfer silently loaded garbage.
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(0xFF8BAC, 0x11);
        bus.WriteByte(0xFF8BAD, 0x22);
        bus.WriteByte(0xFF8BAE, 0x33);
        bus.WriteByte(0xFF8BAF, 0x44);

        bus.WriteWord(0xC00004, RegisterWriteWord(1, 0x10));  // DMA enable
        bus.WriteWord(0xC00004, RegisterWriteWord(15, 2));    // auto-increment = 2 (word-sized writes)
        bus.WriteWord(0xC00004, RegisterWriteWord(19, 2));    // DMA length = 2 words
        bus.WriteWord(0xC00004, RegisterWriteWord(20, 0));
        bus.WriteWord(0xC00004, RegisterWriteWord(21, 0xD6)); // source $7F8BAC >> 1, byte 21
        bus.WriteWord(0xC00004, RegisterWriteWord(22, 0xC5)); // byte 22
        bus.WriteWord(0xC00004, RegisterWriteWord(23, 0x3F)); // byte 23 (mem-to-VDP mode)

        // Destination 0x3000, code = 0x21 (VRAM write | DMA bit) — triggers immediately.
        bus.WriteWord(0xC00004, (ushort)((0x21 & 0x3) << 14 | 0x3000));
        bus.WriteWord(0xC00004, (ushort)(((0x21 >> 2) & 0xF) << 4));

        Assert.Equal(0x11, console.Vdp.Vram[0x3000]);
        Assert.Equal(0x22, console.Vdp.Vram[0x3001]);
        Assert.Equal(0x33, console.Vdp.Vram[0x3002]);
        Assert.Equal(0x44, console.Vdp.Vram[0x3003]);
    }

    [Fact]
    public void RunFrame_RendersActiveScanlinesIntoTheFrameBuffer()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();
        console.Vdp.Registers[1] = 0x40; // display enable
        console.Vdp.Registers[7] = 1;    // backdrop: palette line 0, color index 1
        console.Vdp.Cram[1] = 0x000E;    // pure red

        console.RunFrame();

        Assert.Equal(255, console.Vdp.FrameBuffer[0]); // top-left pixel: backdrop red
    }
}
