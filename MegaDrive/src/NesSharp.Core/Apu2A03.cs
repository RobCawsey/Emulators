using NesSharp.Cpu;

namespace NesSharp.Core;

/// <summary>
/// The Ricoh 2A03's audio side: two pulse channels, triangle, noise, DMC, the frame
/// sequencer that clocks their envelopes/length-counters/sweep on a fixed schedule (and can
/// raise an IRQ), and the non-linear mixer that combines them into a single sample.
///
/// <see cref="Clock"/> must be called exactly once per CPU cycle. Internally: the triangle's
/// timer clocks every CPU cycle; the pulse, noise, and DMC timers clock every *other* CPU
/// cycle (real hardware's "APU cycle" being half the CPU rate) — both facts modeled here,
/// not simplified away. The frame sequencer's cycle counts (7457/14913/22371/29829 for
/// 4-step, plus 37281 for 5-step) are the standard published NTSC values.
///
/// Deliberate simplifications (documented, not overlooked): DMC DMA always stalls the CPU
/// for a flat 4 cycles rather than real hardware's alignment-dependent 4-6; the frame IRQ
/// flag is set for exactly one cycle rather than the real 3-cycle-wide window; mixing uses
/// the standard NTSC lookup-table-equivalent formula but no output filtering (real hardware
/// has a high-pass/low-pass filter chain after the mixer).
/// </summary>
public sealed class Apu2A03
{
    private const double CpuClockHz = 1789773.0;
    private const double OutputSampleRateHz = 44100.0;

    public readonly ApuPulseChannel Pulse1 = new(isPulse1: true);
    public readonly ApuPulseChannel Pulse2 = new(isPulse1: false);
    public readonly ApuTriangleChannel Triangle = new();
    public readonly ApuNoiseChannel Noise = new();
    public readonly ApuDmcChannel Dmc;

    private readonly IBus _bus;

    private bool _halfCycleToggle;
    private int _frameSequencerCycle;
    private bool _fiveStepMode;
    private bool _irqInhibit;
    private bool _frameIrqFlag;

    private double _cyclesUntilNextSample;

    public Queue<float> SampleBuffer { get; } = new();

    public Apu2A03(IBus bus)
    {
        _bus = bus;
        Dmc = new ApuDmcChannel(bus);
        _cyclesUntilNextSample = CpuClockHz / OutputSampleRateHz;
    }

    public void AttachCpu(Cpu6502 cpu) => Dmc.AttachCpu(cpu);

    public bool IrqLine => _frameIrqFlag || Dmc.IrqFlag;

    /// <summary>Advances the APU by one CPU cycle. <paramref name="externalMix"/> is the
    /// cartridge mapper's own expansion audio for this cycle (VRC6/VRC7/FME-7/N106 — see
    /// IMapper.GetAudioSample), summed directly into the analog mix alongside the 2A03's
    /// five channels; 0 for mappers with no expansion audio, which is also the default.</summary>
    public void Clock(float externalMix = 0f)
    {
        Triangle.ClockTimer();
        if (_halfCycleToggle)
        {
            Pulse1.ClockTimer();
            Pulse2.ClockTimer();
            Noise.ClockTimer();
            Dmc.ClockTimer();
        }
        _halfCycleToggle = !_halfCycleToggle;

        ClockFrameSequencer();
        GenerateOutputSample(externalMix);
    }

    private void ClockFrameSequencer()
    {
        _frameSequencerCycle++;
        if (!_fiveStepMode)
        {
            switch (_frameSequencerCycle)
            {
                case 7457: ClockQuarterFrame(); break;
                case 14913: ClockQuarterFrame(); ClockHalfFrame(); break;
                case 22371: ClockQuarterFrame(); break;
                case 29829:
                    ClockQuarterFrame();
                    ClockHalfFrame();
                    if (!_irqInhibit)
                    {
                        _frameIrqFlag = true;
                    }
                    break;
                case 29830:
                    _frameSequencerCycle = 0;
                    break;
            }
        }
        else
        {
            switch (_frameSequencerCycle)
            {
                case 7457: ClockQuarterFrame(); break;
                case 14913: ClockQuarterFrame(); ClockHalfFrame(); break;
                case 22371: ClockQuarterFrame(); break;
                case 37281: ClockQuarterFrame(); ClockHalfFrame(); break;
                case 37282: _frameSequencerCycle = 0; break;
            }
        }
    }

    private void ClockQuarterFrame()
    {
        Pulse1.Envelope.Clock();
        Pulse2.Envelope.Clock();
        Noise.Envelope.Clock();
        Triangle.ClockLinearCounter();
    }

    private void ClockHalfFrame()
    {
        Pulse1.ClockLengthAndSweep();
        Pulse2.ClockLengthAndSweep();
        Noise.ClockLength();
        Triangle.ClockLength();
    }

    public byte ReadStatus()
    {
        byte value = (byte)(
            (Pulse1.LengthCounter > 0 ? 0x01 : 0) |
            (Pulse2.LengthCounter > 0 ? 0x02 : 0) |
            (Triangle.LengthCounter > 0 ? 0x04 : 0) |
            (Noise.LengthCounter > 0 ? 0x08 : 0) |
            (Dmc.BytesRemaining > 0 ? 0x10 : 0) |
            (_frameIrqFlag ? 0x40 : 0) |
            (Dmc.IrqFlag ? 0x80 : 0));
        _frameIrqFlag = false; // reading clears the frame IRQ flag only, not the DMC one
        return value;
    }

    public void WriteStatus(byte value)
    {
        Pulse1.SetEnabled((value & 0x01) != 0);
        Pulse2.SetEnabled((value & 0x02) != 0);
        Triangle.SetEnabled((value & 0x04) != 0);
        Noise.SetEnabled((value & 0x08) != 0);
        Dmc.SetEnabled((value & 0x10) != 0);
    }

    /// <summary>$4017 — frame counter mode/IRQ inhibit. Writing immediately resets the
    /// sequencer, and 5-step mode additionally clocks a quarter- and half-frame right away —
    /// both real, documented behaviors, not simplifications.</summary>
    public void WriteFrameCounter(byte value)
    {
        _fiveStepMode = (value & 0x80) != 0;
        _irqInhibit = (value & 0x40) != 0;
        if (_irqInhibit)
        {
            _frameIrqFlag = false;
        }
        _frameSequencerCycle = 0;
        if (_fiveStepMode)
        {
            ClockQuarterFrame();
            ClockHalfFrame();
        }
    }

    private void GenerateOutputSample(float externalMix)
    {
        int p1 = Pulse1.Output;
        int p2 = Pulse2.Output;
        int tri = Triangle.Output;
        int noise = Noise.Output;
        int dmc = Dmc.Output;

        float pulseOut = p1 + p2 == 0 ? 0f : 95.88f / (8128f / (p1 + p2) + 100f);
        float tndSum = tri / 8227f + noise / 12241f + dmc / 22638f;
        float tndOut = tndSum == 0f ? 0f : 159.79f / (1f / tndSum + 100f);

        _cyclesUntilNextSample -= 1;
        if (_cyclesUntilNextSample <= 0)
        {
            SampleBuffer.Enqueue(pulseOut + tndOut + externalMix);
            _cyclesUntilNextSample += CpuClockHz / OutputSampleRateHz;
        }
    }
}
