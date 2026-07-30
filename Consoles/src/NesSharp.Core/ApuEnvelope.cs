namespace NesSharp.Core;

/// <summary>The decay envelope shared by the two pulse channels and the noise channel.
/// Doubles as that channel's volume divider period when <see cref="ConstantVolume"/> is set,
/// and as the length-counter halt flag when <see cref="LoopFlag"/> is set — both are real
/// hardware's actual bit-sharing in $4000/$4004/$400C, not a simplification here.</summary>
public sealed class ApuEnvelope
{
    public bool StartFlag;
    public bool LoopFlag;
    public bool ConstantVolume;
    public byte VolumeOrPeriod; // 4 bits

    private byte _divider;
    private byte _decayLevel;

    public byte Output => ConstantVolume ? VolumeOrPeriod : _decayLevel;

    public void Clock()
    {
        if (StartFlag)
        {
            StartFlag = false;
            _decayLevel = 15;
            _divider = VolumeOrPeriod;
        }
        else if (_divider == 0)
        {
            _divider = VolumeOrPeriod;
            if (_decayLevel > 0)
            {
                _decayLevel--;
            }
            else if (LoopFlag)
            {
                _decayLevel = 15;
            }
        }
        else
        {
            _divider--;
        }
    }
}
