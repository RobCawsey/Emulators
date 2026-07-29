namespace NesSharp.Core;

/// <summary>One of VRC6's two expansion-audio pulse channels. Unlike the 2A03's pulses,
/// there's no envelope (volume is set directly, 0-15) or sweep, but the duty cycle is a
/// 16-step counter rather than 4 fixed patterns, giving finer duty control; a "digitized
/// mode" bit bypasses the duty entirely (always on) so software can play back PCM samples by
/// rapidly rewriting the volume register. The timer runs at the full CPU clock rate — VRC6
/// has no /2 divider the way the 2A03's pulse/noise channels do.</summary>
public sealed class Vrc6PulseChannel
{
    private byte _volume;
    private byte _duty;
    private bool _digitizedMode;
    private bool _enabled;
    private ushort _period;
    private ushort _counter;
    private int _dutyStep;

    public void WriteControl(byte value)
    {
        _volume = (byte)(value & 0x0F);
        _duty = (byte)((value >> 4) & 0x07);
        _digitizedMode = (value & 0x80) != 0;
    }

    public void WriteFrequencyLow(byte value) => _period = (ushort)((_period & 0x0F00) | value);

    public void WriteFrequencyHigh(byte value)
    {
        _period = (ushort)((_period & 0x00FF) | ((value & 0x0F) << 8));
        _enabled = (value & 0x80) != 0;
    }

    public void ClockTimer()
    {
        if (!_enabled)
        {
            return;
        }
        if (_counter == 0)
        {
            _counter = _period;
            _dutyStep = (_dutyStep + 1) & 0x0F;
        }
        else
        {
            _counter--;
        }
    }

    /// <summary>0-15, before any cross-channel mixing/normalization.</summary>
    public byte Output
    {
        get
        {
            if (!_enabled)
            {
                return 0;
            }
            bool on = _digitizedMode || _dutyStep <= _duty;
            return on ? _volume : (byte)0;
        }
    }
}
