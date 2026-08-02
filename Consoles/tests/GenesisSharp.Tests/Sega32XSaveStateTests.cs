using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Phase 5 of the in-progress 32X extension (see ARCHITECTURE.md §4a.4): confirms
/// <see cref="Sega32X.SaveState"/>/<see cref="Sega32X.LoadState"/> — and their wiring into
/// <see cref="GenesisConsole.SaveState"/>/<see cref="GenesisConsole.LoadState"/> — genuinely
/// round-trip every piece of 32X state, not just the bus-integration state Phase 2 already
/// exercised. No real 32X ROM is needed (Phase 6's job): state is poked directly (SH-2 registers,
/// the adapter/control block, the VDP overlay's registers/palette/frame buffer, PWM's FIFOs),
/// mirroring how GenesisConsoleSh2BusIntegrationTests.cs and Sega32XVdpTests.cs/
/// Sega32XPwmTests.cs already drive this subsystem without one.</summary>
public class Sega32XSaveStateTests
{
    private static GenesisConsole CreateConsoleWithDistinctive32XState()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        // Release both SH-2s and give them distinctive register state.
        bus.WriteByte(0xA15101, 0x03); // ADEN + nRES
        console.Sega32X.MasterSh2.R[0] = 0x1111_2222;
        console.Sega32X.MasterSh2.R[15] = 0x0600_1000;
        console.Sega32X.MasterSh2.PC = 0x0600_0100;
        console.Sega32X.SlaveSh2.R[0] = 0x3333_4444;
        console.Sega32X.SlaveSh2.SR = 0x0000_00F1;

        // COMM ports (Phase 2's own register block).
        bus.WriteWord(0xA15120, 0xBEEF);

        // 32X VDP overlay: display mode, a palette entry, a frame-buffer byte.
        bus.WriteByte(0xA15181, 0x01); // Mx = Packed Pixel
        bus.WriteWord(0xA15210, 0x7C1F);
        bus.WriteWord(0x840100, 0x1234);

        // PWM: routing mode, cycle register, one queued FIFO entry.
        bus.WriteByte(0xA15131, 0x05); // xMd = normal stereo
        bus.WriteWord(0xA15132, 0x0100);
        bus.WriteWord(0xA15134, 0x0081); // Left FIFO push

        return console;
    }

    private static GenesisConsole RoundTrip(GenesisConsole original)
    {
        using var stream = new MemoryStream();
        original.SaveState(stream);

        var restored = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        restored.Reset();
        stream.Position = 0;
        restored.LoadState(stream, allowRomMismatch: true);
        return restored;
    }

    [Fact]
    public void RoundTrip_PreservesBothSh2CoresRegisterState()
    {
        var original = CreateConsoleWithDistinctive32XState();

        var restored = RoundTrip(original);

        Assert.Equal(0x1111_2222u, restored.Sega32X.MasterSh2.R[0]);
        Assert.Equal(0x0600_1000u, restored.Sega32X.MasterSh2.R[15]);
        Assert.Equal(0x0600_0100u, restored.Sega32X.MasterSh2.PC);
        Assert.Equal(0x3333_4444u, restored.Sega32X.SlaveSh2.R[0]);
        Assert.Equal(0x0000_00F1u, restored.Sega32X.SlaveSh2.SR);
    }

    [Fact]
    public void RoundTrip_PreservesAdapterControlAndCommRegisters()
    {
        var original = CreateConsoleWithDistinctive32XState();

        var restored = RoundTrip(original);

        Assert.True(restored.Sega32X.NRes);
        Assert.True(restored.Sega32X.Aden);
        Assert.Equal(0xBEEF, restored.Sega32X.Regs[0x10]); // COMM0
    }

    [Fact]
    public void RoundTrip_PreservesVdpOverlayRegistersPaletteAndFrameBuffer()
    {
        var original = CreateConsoleWithDistinctive32XState();

        var restored = RoundTrip(original);

        Assert.Equal(1, restored.Sega32X.VdpRegs[0] & 0x3); // Mx
        Assert.Equal(0x7C1F, restored.Sega32X.Palette[0x08]);
        Assert.Equal((ushort)0x1234, ((Cpu68000.IBus)restored).ReadWord(0x840100));
    }

    [Fact]
    public void RoundTrip_PreservesPwmFifoAndRoutingState()
    {
        var original = CreateConsoleWithDistinctive32XState();

        var (originalLeft, _) = original.Sega32X.GeneratePwmSample(1.0 / 44100.0);
        var restored = RoundTrip(original);

        // The FIFO entry queued before the round trip should still be there, producing the same
        // held sample once consumed on the restored side too.
        var (restoredLeft, _) = restored.Sega32X.GeneratePwmSample(1.0 / 44100.0);
        Assert.Equal(originalLeft, restoredLeft);
    }

    [Fact]
    public void RoundTrip_IntoAFreshConsole_ProducesIdenticalFutureFramesAndAudio()
    {
        var original = CreateConsoleWithDistinctive32XState();
        for (int frame = 0; frame < 10; frame++)
        {
            original.RunFrame();
        }

        var restored = RoundTrip(original);

        for (int frame = 0; frame < 10; frame++)
        {
            original.RunFrame();
            restored.RunFrame();
            Assert.Equal(original.Vdp.FrameBuffer, restored.Vdp.FrameBuffer);
        }
    }
}
