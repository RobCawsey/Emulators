using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class GenesisConsoleIoTests
{
    /// <summary>ROM filled with 68000 NOPs (0x4E71) so <see cref="M68000.Step"/> can be called
    /// safely to advance <see cref="M68000.TotalCycles"/> by a known amount between bus writes
    /// — see the identical helper elsewhere for why an all-zero ROM isn't safe for this.</summary>
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
    public void SixButtonSequence_UsesTheCpusRealCycleCountForTiming()
    {
        var console = new GenesisConsole(CreateNopRom());
        console.Reset();
        var bus = (Cpu68000.IBus)console;
        console.ControllerPort1.Pad.SixButton = true;
        console.ControllerPort1.Pad.X = true;
        bus.WriteByte(0xA10008, 0x40); // TH is an output

        bus.WriteByte(0xA10002, 0x40); // step 0, TH=1, at Cpu.TotalCycles == 0
        console.Cpu.Step(); console.Cpu.Step(); // advance a few cycles -- well under the timeout
        bus.WriteByte(0xA10002, 0x00); // step 1, TH=0
        console.Cpu.Step(); console.Cpu.Step();
        bus.WriteByte(0xA10002, 0x40); // step 2, TH=1
        console.Cpu.Step(); console.Cpu.Step();
        bus.WriteByte(0xA10002, 0x00); // step 3, TH=0 -- directions forced low
        console.Cpu.Step(); console.Cpu.Step();
        bus.WriteByte(0xA10002, 0x40); // step 4, TH=1 -- X/Y/Z/Mode

        Assert.Equal(0, bus.ReadByte(0xA10002) & 0x01); // X pressed, visible through the 68000 bus
    }

    [Fact]
    public void VersionRegister_ReadsTheFixedDefaultValue()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        Assert.Equal(0xA0, bus.ReadByte(0xA10000));
        Assert.Equal(0xA0, bus.ReadByte(0xA10001)); // either byte of the word aliases the same register
    }

    [Fact]
    public void ControllerPort1_DataAndDirectionRegisters_AreRoutedToThe68000Bus()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;
        console.ControllerPort1.Pad.B = true;

        bus.WriteByte(0xA10008, 0x40); // direction: TH is an output, D0-D5 are inputs
        bus.WriteByte(0xA10002, 0x40); // drive TH=1

        Assert.Equal(0, bus.ReadByte(0xA10002) & 0x10); // B pressed -> line low, visible on the 68000 bus
        Assert.Equal(0x40, console.ControllerPort1.Direction);
    }

    [Fact]
    public void ControllerPort2_IsIndependentOfPort1()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;
        console.ControllerPort2.Pad.Start = true;

        bus.WriteByte(0xA1000A, 0x40);
        bus.WriteByte(0xA10004, 0x00); // TH=0 -- Start shows up at bit 5 here

        Assert.Equal(0, bus.ReadByte(0xA10004) & 0x20);
        // Port 1 unaffected: no buttons pressed, and its TH still defaults to 0 (nothing
        // written there yet), so D2/D3 read forced-low same as any untouched port would.
        Assert.Equal(0x33, bus.ReadByte(0xA10002) & 0x3F);
    }

    [Fact]
    public void ExtPort_DefaultsToDisconnected()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(0xA1000C, 0x40);
        bus.WriteByte(0xA10006, 0x00); // TH=0 -- D2/D3 would be forced low if a pad were connected

        Assert.Equal(0x3F, bus.ReadByte(0xA10006) & 0x3F);
    }

    [Fact]
    public void TmssRegister_RoundTripsWhateverIsWritten()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        bus.WriteLong(0xA14000, 0x53454741); // "SEGA"

        Assert.Equal(0x53454741u, bus.ReadLong(0xA14000));
    }

    /// <summary>Found via a real-ROM hang (Altered Beast, spinning forever on <c>BTST #3,(A6)</c>
    /// with A6 = $C00005, waiting for the VBlank status bit): a byte read at an ODD VDP port
    /// address fell through <see cref="GenesisConsole"/>'s port-offset switch (keyed on the even,
    /// word-aligned offsets 0/2/4/6/8) to its default case, always reading back 0 regardless of
    /// the real status bit — instead of being treated as an alias for the same word register, the
    /// same leniency the controller registers already get.</summary>
    [Fact]
    public void ControlPort_ByteReadAtOddAddress_ReflectsTheRealStatusBit()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        for (int line = 0; line < Vdp.ScreenHeight; line++)
        {
            Assert.Equal(0, bus.ReadByte(0xC00005) & 0x08); // not yet in VBlank
            console.Vdp.AdvanceScanline();
        }

        Assert.True(console.Vdp.InVerticalBlank);
        Assert.Equal(0x08, bus.ReadByte(0xC00005) & 0x08); // VBlank bit, low byte of the status word
    }

    [Fact]
    public void ControlPort_ByteWriteAtOddAddress_ReachesTheVdpRegister()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        // 0x81 duplicated into both bytes of the port word ($8181) is a valid VDP register-write
        // command: top two bits "10" select register-write mode, next six bits (0x01) are the
        // register number, low byte (0x81) is the value -- same command RegisterWriteWord builds
        // in GenesisConsoleTests, just arriving as a single byte instead of a word.
        bus.WriteByte(0xC00005, 0x81);

        Assert.Equal(0x81, console.Vdp.Registers[1]);
    }

    [Fact]
    public void Reset_ClearsTheTmssRegisterAndControllerDirectionRegisters()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;
        bus.WriteLong(0xA14000, 0x53454741);
        bus.WriteByte(0xA10008, 0x40);

        console.Reset();

        Assert.Equal(0u, bus.ReadLong(0xA14000));
        Assert.Equal(0, console.ControllerPort1.Direction);
    }
}
