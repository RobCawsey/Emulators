using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class Ym2612TimerTests
{
    private static void WritePart1(Ym2612 ym, byte address, byte value)
    {
        ym.WriteAddressPart1(address);
        ym.WriteDataPart1(value);
    }

    // Period 0 is Timer A's fastest setting: 1024 ticks * 144 chip clocks/tick, converted to
    // Z80 cycles via the chip/Z80 clock ratio -- comfortably exceeded by 72000.
    private const double TimerACyclesToOverflow = 72000;
    private const int TimerANotYetCycles = 100;

    // Timer B's fastest setting: 256 ticks * 144 * 16 chip clocks/tick.
    private const double TimerBCyclesToOverflow = 276000;
    private const int TimerBNotYetCycles = 1000;

    [Fact]
    public void TimerA_Disabled_NeverOverflows()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        // deliberately never write the start bit in 0x27

        ym.AdvanceTimers(TimerACyclesToOverflow * 10);

        Assert.Equal(0, ym.ReadStatus() & 0x01);
    }

    [Fact]
    public void TimerA_Enabled_EventuallyOverflows()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        WritePart1(ym, 0x27, 0x01); // start Timer A

        Assert.Equal(0, ym.ReadStatus() & 0x01);
        ym.AdvanceTimers(TimerACyclesToOverflow);

        Assert.Equal(0x01, ym.ReadStatus() & 0x01);
    }

    [Fact]
    public void TimerA_DoesNotOverflowBeforeItsPeriodElapses()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        WritePart1(ym, 0x27, 0x01);

        ym.AdvanceTimers(TimerANotYetCycles);

        Assert.Equal(0, ym.ReadStatus() & 0x01);
    }

    [Fact]
    public void TimerA_OverflowFlag_ClearedByControlRegisterBit2()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        WritePart1(ym, 0x27, 0x01);
        ym.AdvanceTimers(TimerACyclesToOverflow);
        Assert.Equal(0x01, ym.ReadStatus() & 0x01);

        WritePart1(ym, 0x27, 0x05); // keep Timer A started (bit0) + clear its overflow flag (bit2)

        Assert.Equal(0, ym.ReadStatus() & 0x01);
    }

    [Fact]
    public void TimerA_AutoReloads_AndOverflowsAgainWithoutRestarting()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        WritePart1(ym, 0x27, 0x01);
        ym.AdvanceTimers(TimerACyclesToOverflow);
        WritePart1(ym, 0x27, 0x05); // clear the flag, timer stays started

        ym.AdvanceTimers(TimerACyclesToOverflow);

        Assert.Equal(0x01, ym.ReadStatus() & 0x01);
    }

    [Fact]
    public void TimerB_Enabled_EventuallyOverflows()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x26, 0x00);
        WritePart1(ym, 0x27, 0x02); // start Timer B

        Assert.Equal(0, ym.ReadStatus() & 0x02);
        ym.AdvanceTimers(TimerBCyclesToOverflow);

        Assert.Equal(0x02, ym.ReadStatus() & 0x02);
    }

    [Fact]
    public void TimerB_DoesNotOverflowBeforeItsPeriodElapses()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x26, 0x00);
        WritePart1(ym, 0x27, 0x02);

        ym.AdvanceTimers(TimerBNotYetCycles);

        Assert.Equal(0, ym.ReadStatus() & 0x02);
    }

    [Fact]
    public void TimerB_OverflowFlag_ClearedByControlRegisterBit3()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x26, 0x00);
        WritePart1(ym, 0x27, 0x02);
        ym.AdvanceTimers(TimerBCyclesToOverflow);
        Assert.Equal(0x02, ym.ReadStatus() & 0x02);

        WritePart1(ym, 0x27, 0x0A); // keep Timer B started (bit1) + clear its overflow flag (bit3)

        Assert.Equal(0, ym.ReadStatus() & 0x02);
    }

    [Fact]
    public void BothTimers_OverflowIndependently()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        WritePart1(ym, 0x27, 0x01); // Timer A only

        ym.AdvanceTimers(TimerACyclesToOverflow);

        Assert.Equal(0x01, ym.ReadStatus()); // bit0 set, bit1 (Timer B, never started) clear
    }

    [Fact]
    public void Reset_ClearsTimerStateAndDisablesBothTimers()
    {
        var ym = new Ym2612();
        WritePart1(ym, 0x24, 0x00);
        WritePart1(ym, 0x25, 0x00);
        WritePart1(ym, 0x27, 0x01);
        ym.AdvanceTimers(TimerACyclesToOverflow);
        Assert.Equal(0x01, ym.ReadStatus() & 0x01);

        ym.Reset();

        Assert.Equal(0, ym.ReadStatus());
        ym.AdvanceTimers(TimerACyclesToOverflow * 10); // stays disabled post-reset without a fresh start write
        Assert.Equal(0, ym.ReadStatus());
    }
}
