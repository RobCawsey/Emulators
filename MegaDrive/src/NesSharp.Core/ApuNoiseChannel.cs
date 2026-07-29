namespace NesSharp.Core;

/// <summary>Pseudo-random noise via a 15-bit linear feedback shift register. Same envelope
/// and length-counter machinery as the pulse channels; no sweep, and no duty cycle — just a
/// period-table-driven timer and a mode bit selecting which two taps feed the shift
/// register.</summary>
public sealed class ApuNoiseChannel
{
    private static readonly byte[] LengthTable =
    {
        10, 254, 20, 2, 40, 4, 80, 6, 160, 8, 60, 10, 14, 12, 26, 14,
        12, 16, 24, 18, 48, 20, 96, 22, 192, 24, 72, 26, 16, 28, 32, 30,
    };

    // NTSC noise timer periods, indexed by the 4-bit period field in $400E.
    private static readonly ushort[] PeriodTable =
    {
        4, 8, 16, 32, 64, 96, 128, 160, 202, 254, 380, 508, 762, 1016, 2034, 4068,
    };

    public readonly ApuEnvelope Envelope = new();

    private ushort _shiftRegister = 1;
    private bool _modeShort;
    private ushort _timerPeriod = PeriodTable[0];
    private ushort _timerCounter;
    private byte _lengthCounter;
    private bool _enabled;

    public byte LengthCounter => _lengthCounter;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
        {
            _lengthCounter = 0;
        }
    }

    /// <summary>$400C — envelope loop (= length halt), constant volume, volume/period.</summary>
    public void WriteControl(byte value)
    {
        Envelope.LoopFlag = (value & 0x20) != 0;
        Envelope.ConstantVolume = (value & 0x10) != 0;
        Envelope.VolumeOrPeriod = (byte)(value & 0x0F);
    }

    /// <summary>$400E — mode flag and period index.</summary>
    public void WritePeriod(byte value)
    {
        _modeShort = (value & 0x80) != 0;
        _timerPeriod = PeriodTable[value & 0x0F];
    }

    /// <summary>$400F — length counter load, restarts envelope.</summary>
    public void WriteLength(byte value)
    {
        if (_enabled)
        {
            _lengthCounter = LengthTable[value >> 3];
        }
        Envelope.StartFlag = true;
    }

    public void ClockTimer()
    {
        if (_timerCounter == 0)
        {
            _timerCounter = _timerPeriod;
            int tapBit = _modeShort ? 6 : 1;
            int feedback = (_shiftRegister & 0x01) ^ ((_shiftRegister >> tapBit) & 0x01);
            _shiftRegister >>= 1;
            _shiftRegister |= (ushort)(feedback << 14);
        }
        else
        {
            _timerCounter--;
        }
    }

    public void ClockLength()
    {
        if (_lengthCounter > 0 && !Envelope.LoopFlag)
        {
            _lengthCounter--;
        }
    }

    /// <summary>0-15, before mixing.</summary>
    public byte Output => !_enabled || _lengthCounter == 0 || (_shiftRegister & 0x01) != 0 ? (byte)0 : Envelope.Output;
}
