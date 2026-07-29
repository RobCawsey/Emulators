namespace NesSharp.Core;

/// <summary>One of the APU's two pulse (square wave) channels. Both are identical except for
/// one sweep-unit asymmetry: pulse 1 computes a negative sweep target with one's complement
/// (an extra -1), pulse 2 with two's complement — a real, documented hardware quirk, not a
/// bug. <see cref="ClockTimer"/> runs at half the CPU rate (the caller only calls it on
/// every other CPU cycle); <see cref="ClockLengthAndSweep"/>/envelope clocking follow the
/// frame sequencer's half-frame/quarter-frame schedule.</summary>
public sealed class ApuPulseChannel
{
    private static readonly byte[][] DutySequences =
    {
        new byte[] { 0, 1, 0, 0, 0, 0, 0, 0 }, // 12.5%
        new byte[] { 0, 1, 1, 0, 0, 0, 0, 0 }, // 25%
        new byte[] { 0, 1, 1, 1, 1, 0, 0, 0 }, // 50%
        new byte[] { 1, 0, 0, 1, 1, 1, 1, 1 }, // 75% (25%, inverted)
    };

    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    };

    private readonly bool _isPulse1;
    public readonly ApuEnvelope Envelope = new();

    private byte _dutyMode;
    private int _dutyStep;
    private ushort _timerPeriod;
    private ushort _timerCounter;
    private byte _lengthCounter;
    private bool _enabled;

    private bool _sweepEnabled;
    private byte _sweepPeriod;
    private bool _sweepNegate;
    private byte _sweepShift;
    private byte _sweepDivider;
    private bool _sweepReload;

    public ApuPulseChannel(bool isPulse1)
    {
        _isPulse1 = isPulse1;
    }

    public byte LengthCounter => _lengthCounter;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _lengthCounter = 0;
        }
    }

    /// <summary>$4000/$4004 — duty, envelope loop (= length halt), constant volume, volume/period.</summary>
    public void WriteControl(byte value)
    {
        _dutyMode = (byte)((value >> 6) & 0x03);
        Envelope.LoopFlag = (value & 0x20) != 0;
        Envelope.ConstantVolume = (value & 0x10) != 0;
        Envelope.VolumeOrPeriod = (byte)(value & 0x0F);
    }

    /// <summary>$4001/$4005 — sweep unit.</summary>
    public void WriteSweep(byte value)
    {
        _sweepEnabled = (value & 0x80) != 0;
        _sweepPeriod = (byte)((value >> 4) & 0x07);
        _sweepNegate = (value & 0x08) != 0;
        _sweepShift = (byte)(value & 0x07);
        _sweepReload = true;
    }

    /// <summary>$4002/$4006 — timer low 8 bits.</summary>
    public void WriteTimerLow(byte value) => _timerPeriod = (ushort)((_timerPeriod & 0x0700) | value);

    /// <summary>$4003/$4007 — timer high 3 bits + length counter load, restarts envelope and duty phase.</summary>
    public void WriteTimerHighAndLength(byte value)
    {
        _timerPeriod = (ushort)((_timerPeriod & 0x00FF) | ((value & 0x07) << 8));
        if (_enabled)
        {
            _lengthCounter = LengthTable[value >> 3];
        }
        _dutyStep = 0;
        Envelope.StartFlag = true;
    }

    public void ClockTimer()
    {
        if (_timerCounter == 0)
        {
            _timerCounter = _timerPeriod;
            _dutyStep = (_dutyStep + 1) % 8;
        }
        else
        {
            _timerCounter--;
        }
    }

    public void ClockLengthAndSweep()
    {
        if (_lengthCounter > 0 && !Envelope.LoopFlag)
        {
            _lengthCounter--;
        }

        int targetPeriod = ComputeSweepTarget();
        bool muted = _timerPeriod < 8 || targetPeriod > 0x7FF;
        if (_sweepDivider == 0 && _sweepEnabled && _sweepShift > 0 && !muted)
        {
            _timerPeriod = (ushort)targetPeriod;
        }
        if (_sweepDivider == 0 || _sweepReload)
        {
            _sweepDivider = _sweepPeriod;
            _sweepReload = false;
        }
        else
        {
            _sweepDivider--;
        }
    }

    private int ComputeSweepTarget()
    {
        int change = _timerPeriod >> _sweepShift;
        if (!_sweepNegate)
        {
            return _timerPeriod + change;
        }
        return _isPulse1 ? _timerPeriod - change - 1 : _timerPeriod - change;
    }

    /// <summary>0-15, before mixing.</summary>
    public byte Output
    {
        get
        {
            bool muted = _timerPeriod < 8 || ComputeSweepTarget() > 0x7FF;
            if (!_enabled || _lengthCounter == 0 || muted || DutySequences[_dutyMode][_dutyStep] == 0)
            {
                return 0;
            }
            return Envelope.Output;
        }
    }
}
