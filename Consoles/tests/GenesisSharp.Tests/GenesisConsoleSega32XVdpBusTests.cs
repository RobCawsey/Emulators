using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Phase 3 of the in-progress 32X extension (see ARCHITECTURE.md §4a.1): proves the new
/// $A15180-$A1519F (vdp_regs), $A15200-$A153FF (palette), and $840000/$860000 (frame buffer)
/// windows are reachable through the real 68000-side <see cref="Cpu68000.IBus"/>, not just
/// through <see cref="Sega32X"/> directly. Mirrors GenesisConsoleSh2BusIntegrationTests.cs's
/// style.</summary>
public class GenesisConsoleSega32XVdpBusTests
{
    private static (GenesisConsole Console, Cpu68000.IBus Bus) CreateConsole()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        return (console, (Cpu68000.IBus)console);
    }

    [Fact]
    public void VdpRegisterWindow_WriteThenReadRoundTrips()
    {
        var (console, bus) = CreateConsole();

        bus.WriteByte(0xA15181, 0x01); // offset 1: Mx = 1 (Packed Pixel)

        Assert.Equal(0x01, bus.ReadByte(0xA15181));
        Assert.Equal(1, console.Sega32X.VdpRegs[0] & 0x3);
    }

    [Fact]
    public void PaletteWindow_WriteThenReadRoundTrips()
    {
        var (console, bus) = CreateConsole();

        bus.WriteWord(0xA15210, 0x7C1F);

        Assert.Equal(0x7C1F, bus.ReadWord(0xA15210));
        Assert.Equal(0x7C1F, console.Sega32X.Palette[0x08]);
    }

    [Fact]
    public void FrameBufferDirectWindow_WriteThenReadRoundTrips()
    {
        var (console, bus) = CreateConsole();

        bus.WriteWord(0x840100, 0x1234);

        Assert.Equal(0x1234, bus.ReadWord(0x840100));
        Assert.Equal(0x1234, bus.ReadWord(0x860100)); // the overwrite window aliases the same bank
    }

    [Fact]
    public void FrameBufferOverwriteWindow_MasksOutZeroBytesUnlikeTheDirectWindow()
    {
        var (console, bus) = CreateConsole();
        bus.WriteWord(0x840200, 0xAABB);

        bus.WriteWord(0x860200, 0x0044); // overwrite window: zero high byte should NOT clobber 0xAA

        Assert.Equal(0xAA44, bus.ReadWord(0x840200));
    }

    [Fact]
    public void FrameBuffer_RendersThroughTheDisplayBank_NotTheWriteBank()
    {
        var (console, bus) = CreateConsole();

        // The CPU write window always targets the write-target bank (opposite of FS); with the
        // default FS=0, that's bank 1 -- bank 0 (the display bank TryGetPixel reads from) is
        // untouched by this write, matching Sega32XVdpTests's own bank-selection coverage.
        bus.WriteWord(0x840000, 0xFFFF);

        Assert.Equal(0xFF, console.Sega32X.FrameBuffer[1][0]);
        Assert.Equal(0xFF, console.Sega32X.FrameBuffer[1][1]);
        Assert.Equal(0x00, console.Sega32X.FrameBuffer[0][0]);
        Assert.Equal(0x00, console.Sega32X.FrameBuffer[0][1]);
    }

    [Fact]
    public void MarsIdRegister_StillReachableAlongsideTheNewWindows()
    {
        // Regression guard: confirms the new predicates added in this phase didn't shadow the
        // existing Phase 2 MARS-string/adapter-register decode.
        var (_, bus) = CreateConsole();

        Assert.Equal((byte)'M', bus.ReadByte(0xA130EC));
        bus.WriteByte(0xA15101, 0x03);
        Assert.Equal(0x03, bus.ReadByte(0xA15101));
    }
}
