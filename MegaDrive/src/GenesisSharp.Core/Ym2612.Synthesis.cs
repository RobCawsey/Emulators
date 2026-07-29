namespace GenesisSharp.Core;

/// <summary>YM2612 FM synthesis: 6 channels, 4 operators each, phase-modulation FM (each
/// operator is a sine oscillator whose phase can be pushed around by another operator's
/// output, per the channel's algorithm), plus a 4-stage (attack/decay/sustain-decay/release)
/// envelope generator and self-feedback on the first operator.
///
/// Confidence varies a lot across this file, so it's worth being explicit about which parts to
/// trust:
/// - The register map (which byte controls what) is widely and consistently documented and
///   recalled with good confidence.
/// - All 8 algorithms' operator connections, including which specific connection in each one
///   routes through the real chip's one-sample-delayed "MEM" register, are now verified against
///   genesis-plus-gx's setup_connection()/chan_calc() (a Nuked-OPN2-derived, cycle-accurate
///   reference) -- see <see cref="AlgorithmModulationSources"/>'s remarks for the specifics.
///   Previously algorithms 1-6 were an unverified "best reconstruction of the general shape"
///   guess, and even algorithm 0 was missing the delay on its middle connection.
/// - Envelope rates now include key-scale-rate (KSR): real hardware speeds up attack/decay
///   /release for higher notes based on each operator's RS field and the channel's key code --
///   see <see cref="ComputeKeyScaleRate"/>. The underlying rate-to-time curve
///   (<see cref="RateToDbPerSecond"/>) is still a smooth exponential approximation, not the real
///   chip's exact non-linear rate table.
/// - Detune now varies by key code too, reproducing the real chip's dt_tab *shape* (each
///   direction's curve normalized to its own peak and scaled by this file's existing calibrated
///   magnitude) rather than a flat cents shift -- see <see cref="ComputeOperatorFrequencyHz"/>.
///   The real table's raw values are 10.10-fixed-point phase increments in units this file can't
///   safely convert to an exact Hz/cents figure without replicating several more layers of the
///   chip's internal fixed-point pipeline, so the *shape* is real but the absolute magnitude is
///   still an anchored approximation.
/// - The modulation/feedback depth scale (<see cref="ModulationDepthScale"/>,
///   <see cref="FeedbackScale"/>) is derived from Nuked-OPN2's fixed-point conventions -- see the
///   remarks above that table.
/// - Not modeled at all: the LFO (AMS/FMS vibrato/tremolo), SSG-EG envelope mode, and channel
///   3's independent-operator-frequency special mode.
/// - DAC/PCM sample playback through channel 6 (registers 0x2A/0x2B) *is* modeled -- see
///   <see cref="GenerateChannelSample"/> -- as a straightforward sample-and-hold: real hardware
///   just outputs whatever 8-bit value was last written to 0x2A, held until the next write, for
///   as long as 0x2B's enable bit is set, entirely replacing (not mixing with) that channel's FM
///   output. No hardware-accurate resampling/interpolation is applied to the held value; the
///   driver's own write rate is what gives it its pitch, same as on real hardware.</summary>
public sealed partial class Ym2612
{
    private enum EnvelopePhase { Attack, Decay1, Decay2, Release, Off }

    private sealed class OperatorState
    {
        public double Phase;
        public EnvelopePhase Envelope = EnvelopePhase.Off;
        public double AttenuationDb = MaxAttenuationDb;
        public double PreviousOutput1;
        public double PreviousOutput2;

        // This slot's own full (envelope-scaled) output from the previous sample -- feeds any
        // algorithm connection real hardware routes through its one-sample-delayed "MEM"
        // register, per AlgorithmModulationSources's Delayed flag.
        public double LastSampleOutput;
        public bool KeyOn;
    }

    // NTSC's 68000-domain clock (master/7, ~7.67MHz) -- the YM2612 shares this clock on
    // Genesis hardware.
    private const double ClockHz = 7_670_454.0;
    private const double MaxAttenuationDb = 96.0;

    // Per FM channel (of 6); see Psg's PeakAmplitude for the shared mixing-budget rationale.
    private const int ChannelPeakAmplitude = 2000;

    // DAC channel (6 only) uses the same peak budget as an FM channel so switching a driver
    // between FM and PCM playback on that channel doesn't itself change its loudness.
    private const int DacPeakAmplitude = 2000;
    private const int DacChannelIndex = 5; // 0-indexed; "channel 6" in 1-indexed chip terms

    // The *peak* detune magnitude for each of the 4 DT field values (0-3) -- real hardware's
    // actual per-note amount is this scaled by DetuneShapeTable's kc-dependent fraction below,
    // not applied flat. Kept as the calibration anchor since the real chip's raw dt_tab values
    // are 10.10-fixed-point phase increments this file can't safely convert to an exact
    // Hz/cents figure (see the type remarks).
    private static readonly double[] DetuneCentsTable = { 0, 5, 10, 20 };

    // Real detune scales with the note's own key code rather than being a flat shift -- this
    // reproduces genesis-plus-gx's dt_tab[4][32] *shape* (each row is the real per-kc weighting,
    // FD=0 is always a no-op). ComputeOperatorFrequencyHz normalizes each row to its own peak
    // (DetuneShapePeak) and scales by DetuneCentsTable, so detune still grows across the
    // keyboard the way real hardware's does while staying anchored to this file's existing
    // calibrated magnitude at the top of each curve.
    private static readonly int[][] DetuneShapeTable =
    {
        new[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 8, 8 },
        new[] { 1, 1, 1, 1, 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 16, 16, 16 },
        new[] { 2, 2, 2, 2, 2, 3, 3, 3, 4, 4, 4, 5, 5, 6, 6, 7, 8, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 20, 22, 22, 22, 22 },
    };
    private static readonly double[] DetuneShapePeak = { 1, 8, 16, 22 }; // FD=0's row is all zero; peak=1 there just avoids a /0

    // Derived (not calibrated-by-ear) from Nuked-OPN2's ym3438.c, a cycle-accurate reference:
    // operator output there is a signed 13-bit value (full scale +-4096) and the phase
    // accumulator is 20-bit, with its upper 10 bits (1024 units) spanning one full cycle.
    // General (non-feedback) modulation sums up to two operators' raw output and shifts right
    // by 1 before adding it straight onto that 10-bit phase; feedback instead shifts the sum by
    // (10 - fb) for fb 1-7 (0 disables it). Converting both to this file's normalized (-1..1)
    // operator output (real/4096) and to phase measured in whole cycles (dividing by 1024):
    // general modulation needs an overall *2.0 (applied once to the summed sources below,
    // regardless of how many of an algorithm's sources feed a given slot -- the real chip
    // always sums exactly two internal paths, tying off unused ones at 0, so the same *2.0
    // applies whether a slot has one modulator or two), and feedback's per-level coefficient on
    // the raw (not averaged) sum of the last two samples is 2^(fb-8). The previous version of
    // this table was an unverified "sounds plausible" guess up to 4x too strong at every
    // feedback level and was implicated in exactly the kind of harsh, chaotic distortion a
    // feedback-heavy percussive patch (e.g. a skid/jump sound effect) would expose far more
    // than a gentler melodic FM voice would.
    private const double ModulationDepthScale = 2.0;
    private static readonly double[] FeedbackScale = { 0, 1.0 / 128, 1.0 / 64, 1.0 / 32, 1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2 };

    /// <summary>For each of the 8 algorithms, which slot(s) feed each slot's modulation input
    /// (empty = none, i.e. this slot only ever receives self-feedback if it's slot 0), and
    /// whether that connection is the real chip's one-sample-delayed "MEM" path. Real hardware
    /// processes slots in order S1,S3,S2,S4 (not S1,S2,S3,S4 -- see
    /// <see cref="SlotRegisterOffset"/>'s remarks for the same register-layout consequence of
    /// this), and every algorithm's connection into logical slot 2 (S3, physically the third
    /// slot processed) is delayed by one sample as a result -- the MEM register holds the value
    /// written by whichever slot feeds it until the *next* sample, rather than the current one.
    /// Verified against genesis-plus-gx's setup_connection()/chan_calc() (itself Nuked-OPN2-
    /// derived and cycle-accurate): for algorithm 3 specifically, only slot 2's OWN contribution
    /// to slot 3 is delayed -- slot 3 also directly (same-sample) reads slot 3's other source.
    /// Always references only lower-indexed slots, so slots can be computed in order 0-3
    /// without any forward references (delayed sources read last *sample's* value regardless of
    /// index, so this ordering constraint is about non-delayed sources only).</summary>
    private static readonly (int Source, bool Delayed)[][][] AlgorithmModulationSources =
    {
        new[] // ALG0: S1->S2->S3->S4, delayed between S2 and S3
        {
            Array.Empty<(int, bool)>(), new[] { (0, false) }, new[] { (1, true) }, new[] { (2, false) },
        },
        new[] // ALG1: S1->S3, S2->S3 (both delayed), S3->S4
        {
            Array.Empty<(int, bool)>(), Array.Empty<(int, bool)>(), new[] { (0, true), (1, true) }, new[] { (2, false) },
        },
        new[] // ALG2: S2->S3 (delayed) ->S4, S1->S4 directly
        {
            Array.Empty<(int, bool)>(), Array.Empty<(int, bool)>(), new[] { (1, true) }, new[] { (0, false), (2, false) },
        },
        new[] // ALG3: S1->S2->S4 (delayed specifically on the S2 contribution), S3->S4 directly
        {
            Array.Empty<(int, bool)>(), new[] { (0, false) }, Array.Empty<(int, bool)>(), new[] { (1, true), (2, false) },
        },
        new[] // ALG4: S1->S2, S3->S4 (two independent carrier chains, no MEM path used)
        {
            Array.Empty<(int, bool)>(), new[] { (0, false) }, Array.Empty<(int, bool)>(), new[] { (2, false) },
        },
        new[] // ALG5: S1 modulates S2/S3/S4 independently -- S3's copy specifically is delayed
        {
            Array.Empty<(int, bool)>(), new[] { (0, false) }, new[] { (0, true) }, new[] { (0, false) },
        },
        new[] // ALG6: S1->S2 (carrier), S3/S4 alone (carriers) -- no MEM path used
        {
            Array.Empty<(int, bool)>(), new[] { (0, false) }, Array.Empty<(int, bool)>(), Array.Empty<(int, bool)>(),
        },
        new[] // ALG7: all four independent carriers
        {
            Array.Empty<(int, bool)>(), Array.Empty<(int, bool)>(), Array.Empty<(int, bool)>(), Array.Empty<(int, bool)>(),
        },
    };

    private static readonly int[][] AlgorithmCarriers =
    {
        new[] { 3 },
        new[] { 3 },
        new[] { 3 },
        new[] { 3 },
        new[] { 1, 3 },
        new[] { 1, 2, 3 },
        new[] { 1, 2, 3 },
        new[] { 0, 1, 2, 3 },
    };

    private readonly OperatorState[][] _operators = CreateOperators();

    private static OperatorState[][] CreateOperators()
    {
        var operators = new OperatorState[6][];
        for (int channel = 0; channel < 6; channel++)
        {
            operators[channel] = new OperatorState[4];
            for (int slot = 0; slot < 4; slot++)
            {
                operators[channel][slot] = new OperatorState();
            }
        }

        return operators;
    }

    private void ResetSynthesisState()
    {
        foreach (var channel in _operators)
        {
            foreach (var op in channel)
            {
                op.Phase = 0;
                op.Envelope = EnvelopePhase.Off;
                op.AttenuationDb = MaxAttenuationDb;
                op.PreviousOutput1 = 0;
                op.PreviousOutput2 = 0;
                op.LastSampleOutput = 0;
                op.KeyOn = false;
            }
        }
    }

    /// <summary>Register 0x28 (Part I only — key on/off is global, not per-part): bits 0-1
    /// select the channel within a part, bit 2 selects part II (channels 3-5), bits 4-7 are
    /// slot 0-3's on/off state.</summary>
    private void HandleKeyOnOff(byte value)
    {
        int channel = ((value & 0x04) != 0 ? 3 : 0) + (value & 0x03);
        if (channel >= 6)
        {
            return;
        }

        for (int slot = 0; slot < 4; slot++)
        {
            bool keyOn = (value & (0x10 << slot)) != 0;
            OperatorState state = _operators[channel][slot];
            if (keyOn && !state.KeyOn)
            {
                state.Envelope = EnvelopePhase.Attack;
            }

            state.KeyOn = keyOn;
        }
    }

    /// <summary>One stereo sample, mixing all 6 FM channels per their L/R enable bits.</summary>
    public (short Left, short Right) GenerateSample(int sampleRateHz)
    {
        int mixLeft = 0;
        int mixRight = 0;

        for (int channel = 0; channel < 6; channel++)
        {
            int output = GenerateChannelSample(channel, sampleRateHz);
            byte panRegister = ChannelRegister(channel, 0xB4);
            if ((panRegister & 0x80) != 0) mixLeft += output;
            if ((panRegister & 0x40) != 0) mixRight += output;
        }

        if (mixLeft is < short.MinValue or > short.MaxValue || mixRight is < short.MinValue or > short.MaxValue)
        {
            ClipCount++;
        }

        return (
            (short)Math.Clamp(mixLeft, short.MinValue, short.MaxValue),
            (short)Math.Clamp(mixRight, short.MinValue, short.MaxValue));
    }

    /// <summary>How many samples this chip's own 6-channel mix has had to hard-clip since the
    /// last reset -- see <see cref="GenesisConsole.AudioClipCount"/>'s remarks for why this
    /// matters as a live diagnostic. Separate from that counter since this one narrows clipping
    /// down to "several FM channels summing past full scale" specifically, before the PSG is
    /// even added on top.</summary>
    public long ClipCount { get; private set; }

    private int GenerateChannelSample(int channel, int sampleRateHz)
    {
        // DAC mode entirely replaces channel 6's FM output on real hardware -- its own
        // operators still hold whatever state they were last in, but nothing reads it while
        // this is active, so there's nothing else to update here.
        if (channel == DacChannelIndex && _dacEnabled)
        {
            return unchecked((sbyte)(_dacSample - 0x80)) * DacPeakAmplitude / 128;
        }

        byte algorithmFeedback = ChannelRegister(channel, 0xB0);
        int algorithm = algorithmFeedback & 0x07;
        int feedback = (algorithmFeedback >> 3) & 0x07;

        // Snapshot last sample's outputs before this sample overwrites them -- delayed
        // modulation sources (see AlgorithmModulationSources) read from here instead of the
        // in-progress opOutput array.
        var previousOutput = new double[4];
        for (int slot = 0; slot < 4; slot++)
        {
            previousOutput[slot] = _operators[channel][slot].LastSampleOutput;
        }

        var opOutput = new double[4];
        for (int slot = 0; slot < 4; slot++)
        {
            opOutput[slot] = ComputeOperator(channel, slot, algorithm, feedback, opOutput, previousOutput, sampleRateHz);
        }

        for (int slot = 0; slot < 4; slot++)
        {
            _operators[channel][slot].LastSampleOutput = opOutput[slot];
        }

        int[] carriers = AlgorithmCarriers[algorithm];
        double mixed = 0;
        foreach (int carrier in carriers)
        {
            mixed += opOutput[carrier];
        }

        // Normalized by carrier count so algorithms with more parallel carriers (e.g. ALG7's
        // four) aren't simply louder than a single-carrier algorithm (e.g. ALG0) purely as an
        // artifact of summing more signals -- real hardware doesn't do this normalization
        // explicitly, but without it, switching algorithms would cause jarring volume jumps.
        mixed /= carriers.Length;

        return (int)(mixed * ChannelPeakAmplitude);
    }

    private double ComputeOperator(int channel, int slot, int algorithm, int feedback, double[] opOutput, double[] previousOutput, int sampleRateHz)
    {
        OperatorState state = _operators[channel][slot];

        // Real hardware sums (up to) two 13-bit raw operator outputs and right-shifts by 1
        // before adding that to the 10-bit phase accumulator (1024 units = one full cycle) --
        // see the ModulationDepthScale remarks for the derivation of the *2.0 here, which
        // converts this file's normalized (-1..1) operator output into that same phase-cycle
        // scale. A delayed source reads last sample's output instead of this one's, per
        // AlgorithmModulationSources's remarks.
        double modulation = 0;
        foreach (var (source, delayed) in AlgorithmModulationSources[algorithm][slot])
        {
            modulation += delayed ? previousOutput[source] : opOutput[source];
        }

        modulation *= ModulationDepthScale;

        if (slot == 0 && feedback > 0)
        {
            modulation += (state.PreviousOutput1 + state.PreviousOutput2) * FeedbackScale[feedback];
        }

        double frequencyHz = ComputeOperatorFrequencyHz(channel, slot);
        state.Phase += frequencyHz / sampleRateHz;
        state.Phase -= Math.Floor(state.Phase);

        double sample = Math.Sin((state.Phase + modulation) * 2 * Math.PI);

        double envelopeAttenuationDb = AdvanceEnvelope(channel, slot, sampleRateHz);
        double totalLevelDb = ReadTotalLevelDb(channel, slot);
        double amplitude = Math.Pow(10, -(envelopeAttenuationDb + totalLevelDb) / 20.0);

        double output = sample * amplitude;
        state.PreviousOutput2 = state.PreviousOutput1;
        state.PreviousOutput1 = output;
        return output;
    }

    /// <summary>Standard OPN frequency formula (F-Number/Block combined with this operator's
    /// Detune+Multiple) — structurally standard, but the detune-to-cents mapping is an
    /// approximation (see the type remarks). Derived from the chip's own published F-Number
    /// formula, Fnumber = 144 * fnote * 2^20 / (fM * 2^(Block-1)), solved for fnote: exponent
    /// <c>21 - Block</c> (verified against smspower.org/Plutiedev's YM2612 register references
    /// — an earlier version of this code briefly "corrected" this to <c>20 - Block</c> on
    /// unreliable memory of the formula, which was wrong and made every note exactly one octave
    /// sharp).</summary>
    private double ComputeOperatorFrequencyHz(int channel, int slot)
    {
        byte fnumHighAndBlock = ChannelRegister(channel, 0xA4);
        byte fnumLow = ChannelRegister(channel, 0xA0);
        int block = (fnumHighAndBlock >> 3) & 0x07;
        int fnum = ((fnumHighAndBlock & 0x07) << 8) | fnumLow;

        double baseFrequencyHz = fnum * ClockHz / (144.0 * Math.Pow(2, 21 - block));

        byte detuneMultiple = OperatorRegister(channel, slot, 0x30);
        int multiple = detuneMultiple & 0x0F;
        double multiplier = multiple == 0 ? 0.5 : multiple;

        int detune = (detuneMultiple >> 4) & 0x07;
        int detuneField = detune & 0x03;
        int keyCode = ComputeKeyCode(channel);
        double detuneShapeFraction = DetuneShapeTable[detuneField][keyCode] / DetuneShapePeak[detuneField];
        double detuneCents = DetuneCentsTable[detuneField] * detuneShapeFraction * ((detune & 0x04) != 0 ? -1 : 1);

        return baseFrequencyHz * multiplier * Math.Pow(2, detuneCents / 1200.0);
    }

    // 1.0dB per step, not the commonly-repeated 0.75dB figure (which turns out to belong to a
    // different chip) -- verified against genesis-plus-gx's own fixed-point math:
    // tl = (v&0x7F) << (ENV_BITS-7) = (v&0x7F)<<3 (8 raw envelope units per TL step), and
    // ENV_STEP = 128.0/ENV_LEN = 128/1024 = 0.125dB per raw unit, so 8*0.125 = 1.0dB/step.
    private double ReadTotalLevelDb(int channel, int slot) => (OperatorRegister(channel, slot, 0x40) & 0x7F) * 1.0;

    /// <summary>Advances this operator's envelope by one sample and returns its current
    /// attenuation in dB (0 = full volume, <see cref="MaxAttenuationDb"/> = silent). The
    /// rate-to-speed curve is a smooth exponential approximation, not the real chip's exact
    /// (and non-linear) rate table — see the type remarks. Key-scale-rate (see
    /// <see cref="ComputeKeyScaleRate"/>) is added to every phase's raw rate field first, so
    /// higher notes attack/decay/release faster, same as real hardware.</summary>
    private double AdvanceEnvelope(int channel, int slot, int sampleRateHz)
    {
        OperatorState state = _operators[channel][slot];
        int keyCode = ComputeKeyCode(channel);
        int keyScaleRate = ComputeKeyScaleRate(channel, slot, keyCode);

        if (state.KeyOn && state.Envelope == EnvelopePhase.Off)
        {
            state.Envelope = EnvelopePhase.Attack;
        }

        if (!state.KeyOn && state.Envelope is not EnvelopePhase.Release and not EnvelopePhase.Off)
        {
            state.Envelope = EnvelopePhase.Release;
        }

        switch (state.Envelope)
        {
            case EnvelopePhase.Attack:
                state.AttenuationDb -= RateToDbPerSecond(ReadRate(channel, slot, 0x50, 0x1F) + keyScaleRate) / sampleRateHz;
                if (state.AttenuationDb <= 0)
                {
                    state.AttenuationDb = 0;
                    state.Envelope = EnvelopePhase.Decay1;
                }

                break;

            case EnvelopePhase.Decay1:
            {
                double sustainDb = ReadSustainLevelDb(channel, slot);
                state.AttenuationDb += RateToDbPerSecond(ReadRate(channel, slot, 0x60, 0x1F) + keyScaleRate) / sampleRateHz;
                if (state.AttenuationDb >= sustainDb)
                {
                    state.AttenuationDb = sustainDb;
                    state.Envelope = EnvelopePhase.Decay2;
                }

                break;
            }

            case EnvelopePhase.Decay2:
                state.AttenuationDb += RateToDbPerSecond(ReadRate(channel, slot, 0x70, 0x1F) + keyScaleRate) / sampleRateHz;
                if (state.AttenuationDb >= MaxAttenuationDb)
                {
                    state.AttenuationDb = MaxAttenuationDb;
                }

                break;

            case EnvelopePhase.Release:
                // The release-rate field is only 4 bits (vs the other three phases' 5 bits) --
                // doubling and adding 1 brings it back into the same effective 0-31 range,
                // matching how this field is commonly described relative to the others.
                int releaseRate4Bit = OperatorRegister(channel, slot, 0x80) & 0x0F;
                state.AttenuationDb += RateToDbPerSecond(releaseRate4Bit * 2 + 1 + keyScaleRate) / sampleRateHz;
                if (state.AttenuationDb >= MaxAttenuationDb)
                {
                    state.AttenuationDb = MaxAttenuationDb;
                    state.Envelope = EnvelopePhase.Off;
                }

                break;

            case EnvelopePhase.Off:
                state.AttenuationDb = MaxAttenuationDb;
                break;
        }

        return state.AttenuationDb;
    }

    private int ReadRate(int channel, int slot, int group, int mask) => OperatorRegister(channel, slot, group) & mask;

    private static double RateToDbPerSecond(int rate) => 3.0 * Math.Pow(2, rate / 3.0);

    // fnum's top 4 bits -> a 0-3 "note fraction" contribution to the key code, per real
    // hardware's kc = (block<<2) | opn_fktable[fnum>>7] (verified against genesis-plus-gx's
    // opn_fktable). Shared by both key-scale-rate and the detune shape table below, since real
    // hardware derives both from this same channel-wide key code.
    private static readonly int[] KeyCodeFractionTable = { 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 3, 3, 3, 3, 3, 3 };

    private int ComputeKeyCode(int channel)
    {
        byte fnumHighAndBlock = ChannelRegister(channel, 0xA4);
        byte fnumLow = ChannelRegister(channel, 0xA0);
        int block = (fnumHighAndBlock >> 3) & 0x07;
        int fnum = ((fnumHighAndBlock & 0x07) << 8) | fnumLow;
        return (block << 2) | KeyCodeFractionTable[fnum >> 7];
    }

    /// <summary>Real hardware's envelope rates aren't just the raw AR/D1R/D2R/RR field -- each
    /// operator's own RS ("rate scale", bits 6-7 of its AR register) additionally speeds them up
    /// for higher notes, via <c>ksr = keyCode >> (3 - RS)</c> (verified against genesis-plus-gx's
    /// set_ar_ksr/eg_rate_select indexing). RS always comes from the AR register specifically,
    /// even when computing D1R/D2R/RR's key-scale-rate -- real hardware doesn't have a separate
    /// RS field per envelope phase.</summary>
    private int ComputeKeyScaleRate(int channel, int slot, int keyCode)
    {
        int rateScale = (OperatorRegister(channel, slot, 0x50) >> 6) & 0x03;
        return keyCode >> (3 - rateScale);
    }

    private double ReadSustainLevelDb(int channel, int slot)
    {
        int sl = (OperatorRegister(channel, slot, 0x80) >> 4) & 0x0F;
        return sl == 15 ? 93.0 : sl * 3.0;
    }

    // Real hardware's per-operator register blocks aren't laid out in the "natural" S1,S2,S3,S4
    // order a 0,1,2,3 slot index would suggest -- register offset +4 is actually Slot 3's data
    // and +8 is Slot 2's (order is S1,S3,S2,S4; verified against smspower.org/Plutiedev's
    // register references). AlgorithmModulationSources/AlgorithmCarriers above use *logical*
    // slot numbers matching how every published algorithm diagram labels them (S1 feeds S2 feeds
    // S3 feeds S4 for ALG0, etc.), so this is the one place that needs to translate a logical
    // slot into the register offset actually holding that operator's detune/multiple/TL/envelope
    // data -- everywhere else in this file can keep using logical slot numbers unchanged.
    private static readonly int[] SlotRegisterOffset = { 0, 8, 4, 12 };

    /// <summary>Per-operator registers are laid out as <paramref name="group"/> +
    /// <see cref="SlotRegisterOffset"/>[slot] + (channel mod 3) — channels 0-2 live in Part I's
    /// register bank, 3-5 in Part II's.</summary>
    private byte OperatorRegister(int channel, int slot, int group) => ChannelRegisterBank(channel)[group + SlotRegisterOffset[slot] + channel % 3];

    /// <summary>Per-channel (not per-operator) registers, e.g. algorithm/feedback and
    /// frequency, addressed the same way but without the slot offset.</summary>
    private byte ChannelRegister(int channel, int group) => ChannelRegisterBank(channel)[group + channel % 3];

    private byte[] ChannelRegisterBank(int channel) => channel < 3 ? _part1Registers : _part2Registers;
}
