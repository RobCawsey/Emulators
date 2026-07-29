namespace NesSharp.Core;

/// <summary>VRC6's sawtooth channel — no equivalent in the built-in APU. An 8-bit
/// accumulator adds a 6-bit rate every other timer underflow, over a 14-underflow cycle (7
/// accumulations), then resets to 0; the result is a rising ramp repeating at the
/// programmed frequency, with high accumulate rates intentionally overflowing the 8-bit
/// accumulator for extra harmonics — a real characteristic of this channel's sound, not a
/// bug. Reconstructed from the documented VRC6 design at reasonable confidence; there's no
/// independent test ROM available to verify the exact accumulation-cycle timing against.
/// </summary>
public sealed class Vrc6SawtoothChannel
{
    private byte _accumRate;
    private bool _enabled;
    private ushort _period;
    private ushort _counter;
    private byte _accumulator;
    private int _phase;

    public void WriteControl(byte value) => _accumRate = (byte)(value & 0x3F);

    public void WriteFrequencyLow(byte value) => _period = (ushort)((_period & 0x0F00) | value);

    public void WriteFrequencyHigh(byte value)
    {
        _period = (ushort)((_period & 0x00FF) | ((value & 0x0F) << 8));
        _enabled = (value & 0x80) != 0;
        if (!_enabled)
        {
            _accumulator = 0;
            _phase = 0;
        }
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
            _phase++;
            if (_phase == 14)
            {
                _phase = 0;
                _accumulator = 0;
            }
            else if (_phase % 2 == 0)
            {
                _accumulator = (byte)(_accumulator + _accumRate);
            }
        }
        else
        {
            _counter--;
        }
    }

    /// <summary>0-31, before any cross-channel mixing/normalization.</summary>
    public byte Output => _enabled ? (byte)(_accumulator >> 3) : (byte)0;
}
