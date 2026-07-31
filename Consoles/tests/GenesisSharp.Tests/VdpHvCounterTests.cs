using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpHvCounterTests
{
    [Fact]
    public void VerticalCounter_LinearBeforeTheJumpPoint()
    {
        var vdp = new Vdp();

        Assert.Equal(0, vdp.VerticalCounter);

        for (int i = 0; i < 0xEA; i++) vdp.AdvanceScanline();
        Assert.Equal(0xEA, vdp.VerticalCounter);
    }

    [Fact]
    public void VerticalCounter_JumpsBackwardsAfterTheNtscV28Breakpoint()
    {
        var vdp = new Vdp();

        for (int i = 0; i < 0xEB; i++) vdp.AdvanceScanline(); // scanline 0xEB (235 decimal)
        Assert.Equal(0xE5, vdp.VerticalCounter);

        for (int i = 0; i < Vdp.LinesPerFrame - 1 - 0xEB; i++) vdp.AdvanceScanline(); // scanline 261, the last line
        Assert.Equal(0xFF, vdp.VerticalCounter);
    }

    [Theory]
    [InlineData(0, 0x00)]      // first dot of the line
    [InlineData(295, 0x93)]    // last visible HC value before the real hardware jump
    [InlineData(296, 0xE9)]    // first visible HC value after the jump (0x94-0xE8 never appear)
    [InlineData(341, 0xFF)]    // last dot of the line
    public void HorizontalCounter_MatchesRealHardwareJumpInH32(int dot, int expectedHc)
    {
        // Confirmed against genesis-plus-gx's cycle2hc32 table (core/hvc.h): H32's visible HC
        // range is 0x00-0x93, then jumps straight to 0xE9-0xFF.
        var vdp = new Vdp(); // Is40CellMode defaults false -> H32, 342 total dots
        vdp.SetScanlineProgress((dot + 0.5) / 342.0);

        Assert.Equal(expectedHc, vdp.HorizontalCounter);
    }

    [Theory]
    [InlineData(0, 0x00)]      // first dot of the line
    [InlineData(364, 0xB6)]    // last visible HC value before the real hardware jump
    [InlineData(365, 0xE4)]    // first visible HC value after the jump (0xB7-0xE3 never appear)
    [InlineData(419, 0xFF)]    // last dot of the line
    public void HorizontalCounter_MatchesRealHardwareJumpInH40(int dot, int expectedHc)
    {
        // Confirmed against genesis-plus-gx's cycle2hc40 table (core/hvc.h): H40's visible HC
        // range is 0x00-0xB6, then jumps straight to 0xE4-0xFF.
        var vdp = new Vdp();
        vdp.Registers[12] = 0x01; // H40
        vdp.SetScanlineProgress((dot + 0.5) / 420.0);

        Assert.Equal(expectedHc, vdp.HorizontalCounter);
    }

    [Fact]
    public void IsInHorizontalBlank_FalseWhileWithinTheActiveWidth_TrueOnceItsExceeded()
    {
        var vdp = new Vdp(); // H32: 256 active out of 342 total dots

        vdp.SetScanlineProgress(0.7); // dot 239 -- comfortably inside the active region
        Assert.False(vdp.IsInHorizontalBlank);

        vdp.SetScanlineProgress(0.8); // dot 273 -- comfortably past it
        Assert.True(vdp.IsInHorizontalBlank);
    }

    [Fact]
    public void ReadHvCounter_CombinesVInHighByteAndHInLowByte()
    {
        var vdp = new Vdp();
        for (int i = 0; i < 10; i++) vdp.AdvanceScanline(); // V = 10
        vdp.SetScanlineProgress(0.5); // H32, dot 171 -> HC 0x55 (85)

        Assert.Equal((ushort)((10 << 8) | 85), vdp.ReadHvCounter());
    }

    [Fact]
    public void StatusRegister_HBlankBitTracksHorizontalPosition_AndIsNotClearedByReading()
    {
        var vdp = new Vdp();

        vdp.SetScanlineProgress(0.7);
        Assert.Equal(0, vdp.ReadStatusRegister() & 0x0004);

        vdp.SetScanlineProgress(0.8);
        Assert.Equal(0x0004, vdp.ReadStatusRegister() & 0x0004);
        Assert.Equal(0x0004, vdp.ReadStatusRegister() & 0x0004); // reading again still shows it -- a live level, not a latched flag
    }

    [Fact]
    public void HorizontalInterrupt_FiresEveryRegisterTenPlusOneActiveLines()
    {
        var vdp = new Vdp();
        vdp.Registers[0] = 0x10; // enable H interrupts
        vdp.Registers[10] = 2;   // fires every 3rd active-line tick

        // The countdown only actually adopts register 10's value at a reload point (an
        // underflow, or a frame wrap) — a freshly constructed Vdp starts it at 0, not at
        // whatever register 10 happens to hold. Drive through one full frame first so the
        // explicit frame-wrap reload (see AdvanceScanline) seeds it cleanly before measuring.
        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();

        int fireCount = 0;
        vdp.HorizontalInterruptRequested += () => fireCount++;

        vdp.AdvanceScanline();
        vdp.AdvanceScanline();
        Assert.Equal(0, fireCount);
        vdp.AdvanceScanline();
        Assert.Equal(1, fireCount);

        vdp.AdvanceScanline();
        vdp.AdvanceScanline();
        Assert.Equal(1, fireCount);
        vdp.AdvanceScanline();
        Assert.Equal(2, fireCount);
    }

    [Fact]
    public void HorizontalInterrupt_DoesNotFireWhenDisabled()
    {
        var vdp = new Vdp();
        vdp.Registers[0] = 0x00; // disabled
        vdp.Registers[10] = 0;   // would fire every line if it were enabled
        int fireCount = 0;
        vdp.HorizontalInterruptRequested += () => fireCount++;

        for (int i = 0; i < 10; i++) vdp.AdvanceScanline();

        Assert.Equal(0, fireCount);
    }

    [Fact]
    public void HorizontalInterrupt_StopsDuringVerticalBlank()
    {
        var vdp = new Vdp();
        vdp.Registers[0] = 0x10;
        vdp.Registers[10] = 0; // fires every active-line tick
        int fireCount = 0;
        vdp.HorizontalInterruptRequested += () => fireCount++;

        for (int i = 0; i < Vdp.ScreenHeight; i++) vdp.AdvanceScanline(); // reach the start of vblank
        int fireCountAtVblank = fireCount;
        Assert.True(fireCountAtVblank > 0);

        for (int i = 0; i < 20; i++) vdp.AdvanceScanline(); // stay within vertical blanking
        Assert.Equal(fireCountAtVblank, fireCount); // no further fires while blanking
    }

    [Fact]
    public void HorizontalInterrupt_CountdownReloadsExactlyAtEachFrameWrap()
    {
        // Register 10 = 100 needs 101 ticks to underflow. One frame's active region gives 223
        // ticks, so it fires twice (at tick 101 and 202) with 21 leftover ticks -- if the
        // countdown weren't explicitly reloaded at the frame wrap, it would carry over at 79
        // (100-21) into frame 2, and the next fire would arrive after only 80 more ticks
        // instead of a fresh 101.
        var vdp = new Vdp();
        vdp.Registers[0] = 0x10;
        vdp.Registers[10] = 100;

        // Prime through one frame first so the countdown starts this test's measured frame
        // freshly reloaded to 100 (see the comment in the previous test for why a freshly
        // constructed Vdp can't be trusted to start there on its own).
        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();

        int fireCount = 0;
        vdp.HorizontalInterruptRequested += () => fireCount++;

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline(); // one full frame, wraps back to scanline 0
        Assert.Equal(2, fireCount);

        for (int i = 0; i < 100; i++) vdp.AdvanceScanline();
        Assert.Equal(2, fireCount); // still no fire -- proves the countdown was reloaded to a fresh 100, not left at 79

        vdp.AdvanceScanline(); // the 101st tick into frame 2
        Assert.Equal(3, fireCount);
    }
}
