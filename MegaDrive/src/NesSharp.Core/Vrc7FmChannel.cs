namespace NesSharp.Core;

/// <summary>One of VRC7's 6 FM channels: a modulator operator feeding a carrier operator
/// (2-operator FM, with optional feedback from the modulator into itself) — see
/// <see cref="Vrc7FmOperator"/> for what's simplified vs. real hardware.</summary>
public sealed class Vrc7FmChannel
{
    public readonly Vrc7FmOperator Modulator = new();
    public readonly Vrc7FmOperator Carrier = new();
    public byte Feedback;
    public ushort FNumber;
    public byte Block;
    public byte Volume;

    private bool _keyOn;
    private float _prevModulatorOutput;

    private const double FrequencyScale = 0.05;

    public void SetKeyOn(bool on)
    {
        if (on && !_keyOn)
        {
            Modulator.KeyOn();
            Carrier.KeyOn();
        }
        else if (!on && _keyOn)
        {
            Modulator.KeyOff();
            Carrier.KeyOff();
        }
        _keyOn = on;
    }

    public void Clock(double sampleRateHz)
    {
        double freq = FNumber * FrequencyScale * Math.Pow(2, Block);
        Modulator.AdvancePhase(freq, sampleRateHz);
        Carrier.AdvancePhase(freq, sampleRateHz);
        Modulator.ClockEnvelope();
        Carrier.ClockEnvelope();
    }

    public float Output()
    {
        float feedbackInput = Feedback > 0 ? _prevModulatorOutput * (Feedback / 7f) * 0.5f : 0f;
        float modOut = Modulator.Output(feedbackInput);
        _prevModulatorOutput = modOut;
        float carOut = Carrier.Output(modOut * 2.0);
        return carOut * (Volume / 15f);
    }
}
