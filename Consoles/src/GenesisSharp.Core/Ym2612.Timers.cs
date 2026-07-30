namespace GenesisSharp.Core;

/// <summary>YM2612 Timer A/B: up-counters, loaded from a programmable starting value and
/// auto-reloading on overflow, that report their overflow via <see cref="Ym2612.ReadStatus"/>'s
/// low two bits. This is
/// the *only* way Genesis sound drivers commonly detect a tick from this chip, since (per
/// <see cref="Ym2612.ReadStatus"/>'s remarks) real hardware never wires either timer's overflow
/// to the Z80's interrupt line -- a driver that starts a timer and then polls the status
/// register for it isn't a bug or an unusual pattern, it's the normal, expected way to use this
/// chip on a Genesis specifically. Confidence: the register map and general "10-bit up-counter,
/// auto-reload on overflow" behavior are well-documented and trustworthy; the exact prescaler
/// values (<see cref="TimerATickDivisor"/>, <see cref="TimerBTickMultiplier"/>) are commonly
/// cited figures, not independently verified against real silicon, so the *exact* real-time
/// period a driver sees may be slightly off even though the overflow behavior itself is
/// correct.</summary>
public sealed partial class Ym2612
{
    // The YM2612's own input clock -- same figure Ym2612.Synthesis.cs's FM engine uses -- and
    // the Z80's clock, which AdvanceTimers's caller reports elapsed time in terms of.
    private const double TimerClockHz = 7_670_454.0;
    private const double Z80ClockHz = 3_579_545.0;

    // Timer A ticks once every 144 chip-clock cycles (verified against genesis-plus-gx's
    // ym2612.c: its own eg_timer -- incremented at the exact same per-sample point as
    // INTERNAL_TIMER_A() -- is explicitly commented as running at "chipclock/144/3", and the FM
    // engine's own sample rate is chipclock/144 throughout the OPN2 family). Timer B ticks 16x
    // slower than that. Previously 12.0, an unverified guess that made Timer A -- the standard
    // "music tick" source most Genesis sound drivers poll directly -- run 12x too fast.
    private const double TimerATickDivisor = 144.0;
    private const double TimerBTickMultiplier = 16.0;

    private int _timerACounter; // 0-1023
    private double _timerATickAccumulator;
    private bool _timerAEnabled;
    private bool _timerAOverflow;

    private int _timerBCounter; // 0-255
    private double _timerBTickAccumulator;
    private bool _timerBEnabled;
    private bool _timerBOverflow;

    private void ResetTimers()
    {
        _timerACounter = 0;
        _timerATickAccumulator = 0;
        _timerAEnabled = false;
        _timerAOverflow = false;

        _timerBCounter = 0;
        _timerBTickAccumulator = 0;
        _timerBEnabled = false;
        _timerBOverflow = false;
    }

    /// <summary>Register 0x27: bit0/bit1 start (and, on a 0->1 transition, reload) Timer A/B;
    /// bit2/bit3 clear a latched overflow flag. Bits 4-7 (timer IRQ enables, channel 3 special
    /// mode) aren't modeled -- the IRQ enable bits are moot since nothing here ever raises a Z80
    /// interrupt from a timer regardless (see <see cref="Ym2612.ReadStatus"/>).</summary>
    private void HandleTimerControlWrite(byte value)
    {
        bool startA = (value & 0x01) != 0;
        if (startA && !_timerAEnabled)
        {
            LoadTimerACounter();
        }
        _timerAEnabled = startA;

        bool startB = (value & 0x02) != 0;
        if (startB && !_timerBEnabled)
        {
            LoadTimerBCounter();
        }
        _timerBEnabled = startB;

        if ((value & 0x04) != 0) _timerAOverflow = false;
        if ((value & 0x08) != 0) _timerBOverflow = false;
    }

    /// <summary>10-bit period: register 0x24 holds the top 8 bits, 0x25's low 2 bits hold the
    /// rest. Loaded fresh from whatever the period registers currently hold -- real hardware
    /// doesn't snapshot the period at some earlier point, only when the timer (re)starts.</summary>
    private void LoadTimerACounter() => _timerACounter = (_part1Registers[0x24] << 2) | (_part1Registers[0x25] & 0x03);

    private void LoadTimerBCounter() => _timerBCounter = _part1Registers[0x26];

    /// <summary>Advances both timers by <paramref name="z80Cycles"/> worth of real time,
    /// converted into this chip's own clock domain -- called once per scanline from
    /// <see cref="GenesisConsole"/> regardless of whether the Z80 CPU itself is currently
    /// stepping, since the chip's internal clock keeps running independent of Z80 bus
    /// arbitration (a bus-requested/held Z80 doesn't freeze this chip on real hardware
    /// either).</summary>
    public void AdvanceTimers(double z80Cycles)
    {
        double chipClocks = z80Cycles * (TimerClockHz / Z80ClockHz);

        if (_timerAEnabled)
        {
            _timerATickAccumulator += chipClocks / TimerATickDivisor;
            while (_timerATickAccumulator >= 1.0)
            {
                _timerATickAccumulator -= 1.0;
                _timerACounter++;
                if (_timerACounter >= 1024)
                {
                    _timerAOverflow = true;
                    LoadTimerACounter(); // auto-reload while still enabled
                }
            }
        }

        if (_timerBEnabled)
        {
            _timerBTickAccumulator += chipClocks / (TimerATickDivisor * TimerBTickMultiplier);
            while (_timerBTickAccumulator >= 1.0)
            {
                _timerBTickAccumulator -= 1.0;
                _timerBCounter++;
                if (_timerBCounter >= 256)
                {
                    _timerBOverflow = true;
                    LoadTimerBCounter();
                }
            }
        }
    }
}
