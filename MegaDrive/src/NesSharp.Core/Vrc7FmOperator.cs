namespace NesSharp.Core;

/// <summary>
/// One operator (modulator or carrier) of a VRC7 FM channel: a sine oscillator with a
/// 4-stage attack/decay/sustain/release envelope. This is a real, working FM synthesis
/// building block — but a simplified one. Real OPLL/YM2413 hardware generates its sine wave
/// from a log-domain ROM table (giving FM synthesis its characteristic slightly "digital"
/// timbre) and shapes each envelope stage with exponential rate curves that also depend on
/// the note's pitch (key scaling); this uses a plain floating-point sine and linear envelope
/// ramps instead. Functionally real FM synthesis, not a hardware-accurate reproduction.
/// </summary>
public sealed class Vrc7FmOperator
{
    private enum Stage { Attack, Decay, Sustain, Release, Idle }

    public byte Multiplier = 1;
    public byte AttackRate;
    public byte DecayRate;
    public byte SustainLevel; // 0 (quietest) .. 15 (loudest) — linear, unlike real OPLL's inverted attenuation scale
    public byte ReleaseRate;
    public bool SustainMode; // hold at SustainLevel while keyed on, instead of continuing to decay toward 0
    public byte TotalLevel; // 0 (loudest) .. 63 (quietest)
    public byte Waveform; // 0 = sine, 1 = a distinct crude second waveform (rectified sine)

    public double Phase;
    private float _envelope;
    private Stage _stage = Stage.Idle;

    public void KeyOn()
    {
        _stage = Stage.Attack;
    }

    public void KeyOff()
    {
        if (_stage != Stage.Idle)
        {
            _stage = Stage.Release;
        }
    }

    public void ClockEnvelope()
    {
        switch (_stage)
        {
            case Stage.Attack:
                _envelope += RateToStep(AttackRate);
                if (_envelope >= 1f)
                {
                    _envelope = 1f;
                    _stage = Stage.Decay;
                }
                break;
            case Stage.Decay:
            {
                float sustainTarget = SustainLevel / 15f;
                _envelope -= RateToStep(DecayRate);
                if (_envelope <= sustainTarget)
                {
                    _envelope = sustainTarget;
                    _stage = Stage.Sustain;
                }
                break;
            }
            case Stage.Sustain:
                if (!SustainMode)
                {
                    _envelope -= RateToStep(DecayRate) * 0.1f;
                    if (_envelope <= 0f)
                    {
                        _envelope = 0f;
                        _stage = Stage.Idle;
                    }
                }
                break;
            case Stage.Release:
                _envelope -= RateToStep(ReleaseRate);
                if (_envelope <= 0f)
                {
                    _envelope = 0f;
                    _stage = Stage.Idle;
                }
                break;
        }
    }

    private static float RateToStep(byte rate) => rate == 0 ? 0f : rate / 15f * 0.02f;

    public void AdvancePhase(double frequencyHz, double sampleRateHz)
    {
        Phase += frequencyHz * Multiplier / sampleRateHz;
        Phase -= Math.Floor(Phase);
    }

    /// <summary>modulationInput is added to the phase before evaluating the waveform —
    /// this is literally what makes it "frequency modulation": the modulator operator's
    /// output shifts the carrier's instantaneous phase.</summary>
    public float Output(double modulationInput)
    {
        double sample = Math.Sin((Phase + modulationInput) * 2 * Math.PI);
        if (Waveform == 1)
        {
            sample = Math.Abs(sample);
        }
        float attenuation = 1f - TotalLevel / 63f;
        return (float)sample * _envelope * attenuation;
    }
}
