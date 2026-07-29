namespace NesSharp.Core;

/// <summary>The triangle channel: a 32-step ramp (no envelope — always full amplitude or
/// silent), gated by both a length counter and a separate linear counter. Unlike the pulse
/// and noise channels, its timer is clocked every CPU cycle, not every other one — a real
/// hardware asymmetry the caller (Apu2A03) accounts for.</summary>
public sealed class ApuTriangleChannel
{
    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    };

    // 15,14,...,0,0,1,...,15 — descending then ascending.
    private static readonly byte[] Sequence = BuildSequence();

    private static byte[] BuildSequence()
    {
        var seq = new byte[32];
        for (int i = 0; i < 16; i++)
        {
            seq[i] = (byte)(15 - i);
            seq[i + 16] = (byte)i;
        }
        return seq;
    }

    private ushort _timerPeriod;
    private ushort _timerCounter;
    private int _sequenceStep;
    private byte _lengthCounter;
    private bool _enabled;

    private byte _linearCounter;
    private byte _linearCounterReload;
    private bool _linearCounterControl; // also doubles as the length-counter halt flag
    private bool _linearCounterReloadFlag;

    public byte LengthCounter => _lengthCounter;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _lengthCounter = 0;
        }
    }

    /// <summary>$4008 — length counter halt / linear counter control, and reload value.</summary>
    public void WriteControl(byte value)
    {
        _linearCounterControl = (value & 0x80) != 0;
        _linearCounterReload = (byte)(value & 0x7F);
    }

    public void WriteTimerLow(byte value) => _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);

    /// <summary>$400B — timer high 3 bits + length counter load, sets the linear counter reload flag.</summary>
    public void WriteTimerHighAndLength(byte value)
    {
        _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
        if (_enabled)
        {
            _lengthCounter = LengthTable[value >> 3];
        }
        _linearCounterReloadFlag = true;
    }

    public void ClockTimer()
    {
        if (_timerCounter == 0)
        {
            _timerCounter = _timerPeriod;
            // A timer period of 0 or 1 produces an inaudible ultrasonic frequency real
            // software sometimes uses to silence the channel without disabling it — advancing
            // the sequence here matches hardware; muting isn't needed for correctness.
            if (_lengthCounter > 0 && _linearCounter > 0)
            {
                _sequenceStep = (_sequenceStep + 1) % 32;
            }
        }
        else
        {
            _timerCounter--;
        }
    }

    public void ClockLinearCounter()
    {
        if (_linearCounterReloadFlag)
        {
            _linearCounter = _linearCounterReload;
        }
        else if (_linearCounter > 0)
        {
            _linearCounter--;
        }
        if (!_linearCounterControl)
        {
            _linearCounterReloadFlag = false;
        }
    }

    public void ClockLength()
    {
        if (_lengthCounter > 0 && !_linearCounterControl)
        {
            _lengthCounter--;
        }
    }

    /// <summary>0-15, before mixing. Strictly, real hardware only freezes the sequencer (at
    /// whatever step it stopped on, which need not be silence) when a counter hits zero,
    /// rather than forcing output to 0 — but well-behaved software avoids leaving the
    /// sequencer parked on a non-zero step, so treating either counter hitting zero as
    /// silence (the common practical simplification) matches real audio output for all but
    /// a deliberately contrived edge case.</summary>
    public byte Output => !_enabled || _lengthCounter == 0 || _linearCounter == 0 ? (byte)0 : Sequence[_sequenceStep];
}
