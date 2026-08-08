namespace GenesisSharp.Core;

/// <summary>Phase 4 of the in-progress Sega 32X extension (see ARCHITECTURE.md §4a.3) — PWM
/// (Pulse Width Modulation) audio. Despite the name, this is not real duty-cycle waveform
/// synthesis: PicoDrive's own <c>convert_sample</c> treats each FIFO entry as a linear amplitude
/// value and rescales it directly into a PCM-ish sample (<c>pwm.c:60-65</c>) — this core does the
/// same.
///
/// Ground truth: <c>reference/PicoDrive/picodrive/pico/32x/pwm.c</c>, cited per-member below.
/// Registers live inside the same <see cref="Regs"/> block Phase 2 already wired end-to-end
/// (bytes 0x30-0x3f), so no new bus predicates or address windows are needed this phase — see
/// <see cref="Sega32X.cs"/>'s <c>WriteControlByteFrom68k</c>/<c>WriteRegisterByteFromSh2</c>/
/// <c>ReadControlByteFor68k</c>/<c>ReadControlByteForSh2</c>, which dispatch offsets ≥ 0x30 here.
///
/// Unlike PicoDrive, which generates PWM into an internal ring buffer continuously as the SH-2
/// executes and resamples that buffer once per host audio callback, this core generates exactly
/// one output sample per call to <see cref="GeneratePwmSample"/> — matching how <see cref="Psg"/>/
/// <see cref="Ym2612"/> already work. <see cref="GeneratePwmSample"/> tracks its own elapsed-cycle
/// debt and only actually consumes a FIFO entry once that debt crosses the current sample period,
/// otherwise it returns the previously-held value — which reproduces PicoDrive's sample-and-hold
/// decimation *and* its underrun-holds-last-value behavior for free, with no separate ring buffer
/// or resampling step needed.</summary>
public sealed partial class Sega32X
{
    private const int PwmControlIndex = 0x18; // byte offset 0x30/0x31
    private const int PwmCycleIndex = 0x19;   // byte offset 0x32/0x33
    private const int PwmLeftIndex = 0x1A;    // byte offset 0x34/0x35 (Left/Mono FIFO)
    private const int PwmRightIndex = 0x1B;   // byte offset 0x36/0x37 (Right FIFO)
    private const int PwmMonoIndex = 0x1C;    // byte offset 0x38/0x39 (Mono alias -> Left FIFO)

    /// <summary>RTP, control-register bit 7 (<c>P32XP_RTP</c>, pico_int.h:615) — SH-2-writable
    /// only. When set, every PWM interrupt also asserts the chip's DMA request line, which is how
    /// real software keeps the FIFO fed without hand-writing each sample. See
    /// <see cref="RaisePwmInterrupt"/>.</summary>
    private const ushort PwmRtpBit = 1 << 7;

    private const byte PwmFullBit = 1 << 7;  // high byte of a status read; P32XP_FULL (1<<15 of the word), pico_int.h:616
    private const byte PwmEmptyBit = 1 << 6; // P32XP_EMPTY (1<<14 of the word), pico_int.h:617

    private const int PwmFifoDepth = 3; // effective queue depth confirmed against pwm_p==3 => FULL (pwm.c:187-188,195-196)

    // The 68000's own clock -- GenesisConsole.cs and Ym2612.Synthesis.cs each keep their own copy
    // of this same figure rather than sharing a cross-class reference; this follows that
    // established convention.
    private const double M68kClockHz = 7_670_454.0;

    private readonly ushort[][] _pwmFifo = { new ushort[PwmFifoDepth], new ushort[PwmFifoDepth] };
    private readonly int[] _pwmFifoCount = new int[2];
    private readonly int[] _pwmFifoHead = new int[2];
    private readonly short[] _pwmCurrent = new short[2];
    private double _pwmCycleDebt;

    /// <summary>Counts down once per sample actually dequeued (both channels together, matching
    /// PicoDrive's own single shared counter -- <see cref="Sega32X.RaisePwmInterrupt"/>'s own
    /// remarks), reloaded from <see cref="ComputePwmIrqReload"/> whenever it reaches zero. Not
    /// reloaded eagerly on every control-register write the way PicoDrive's own
    /// <c>p32x_pwm_ctl_changed</c> does (<c>pwm.c:25-47</c>) -- this core just recomputes the
    /// reload value fresh from the live register each time it's actually needed, a simplification
    /// available since nothing here needs PicoDrive's own event-scheduler integration.</summary>
    private int _pwmIrqCounter;

    private void ResetPwm()
    {
        Array.Clear(_pwmFifo[0]);
        Array.Clear(_pwmFifo[1]);
        Array.Clear(_pwmFifoCount);
        Array.Clear(_pwmFifoHead);
        Array.Clear(_pwmCurrent);
        _pwmCycleDebt = 0;
        _pwmIrqCounter = 0; // reloaded from the live control register the first time it's needed
    }

    /// <summary>Confirmed against PicoDrive's own <c>p32x_pwm_ctl_changed</c> (<c>pwm.c:40-42</c>):
    /// the IRQ-timer nibble (control register bits 8-11, SH-2-exclusive -- see <see
    /// cref="WritePwmByte"/>'s own remarks) is normalized <c>((n - 1) &amp; 0x0f) + 1</c>, mapping
    /// the nibble's 16 possible values onto a 1-16 (not 0-15) reload range -- 0 does *not* mean
    /// "never reload" or "reload every sample", it wraps to 16.</summary>
    private int ComputePwmIrqReload()
    {
        int irqTimer = (Regs[PwmControlIndex] & 0x0F00) >> 8;
        return ((irqTimer - 1) & 0x0F) + 1;
    }

    /// <summary>Advances PWM's own sample clock by <paramref name="sampleDurationSeconds"/> and
    /// returns this call's stereo output, ready to mix directly into
    /// <see cref="GenesisConsole.GenerateAudioSample"/> alongside PSG/YM2612. Returns silence
    /// whenever <c>xMd</c> is one of the confirmed-invalid routing values — including the
    /// power-on default of 0 — so a non-32X ROM always gets exactly <c>(0, 0)</c>.</summary>
    public (short Left, short Right) GeneratePwmSample(double sampleDurationSeconds)
    {
        AdvancePwmClock(sampleDurationSeconds);

        int xmd = Regs[PwmControlIndex] & 0x0F; // confirmed: bits 0-3 of the control register, pwm.c:277
        if (xmd is 0x00 or 0x06 or 0x09 or 0x0F) // confirmed-invalid, pwm.c:278-279
        {
            return (0, 0);
        }

        if (xmd == 0x0A) // swapped stereo, pwm.c:299-309
        {
            return (_pwmCurrent[1], _pwmCurrent[0]);
        }

        if (xmd == 0x05) // normal stereo, pwm.c:288-298
        {
            return (_pwmCurrent[0], _pwmCurrent[1]);
        }

        // Every other non-invalid nibble: PicoDrive's own more elaborate LMD/RMD bit-field
        // routing (pwm.c:311-315) isn't fully documented even in its own source ("// invalid?",
        // pwm.c:279) -- treated here as mono (both channels get the held Left/Mono value), a
        // deliberate simplification rather than a confirmed hardware fact.
        short mono = _pwmCurrent[0];
        return (mono, mono);
    }

    /// <summary>Confirmed against PicoDrive's own PWM timing (<c>pwm.c:68</c> &amp;c.): sample
    /// consumption is clocked at exactly 3× the 68000's clock — the fixed SH-2:68000 hardware
    /// ratio — deliberately *not* whatever throttled SH-2 execution-speed multiplier this core
    /// might use for instruction stepping (PicoDrive itself keeps these two clocks separate for
    /// the same reason, <c>pico.h:299-302</c>, <c>32x.c:670-672</c>).</summary>
    private void AdvancePwmClock(double sampleDurationSeconds)
    {
        _pwmCycleDebt += sampleDurationSeconds * M68kClockHz * 3.0;

        int cycles = (Regs[PwmCycleIndex] - 1) & 0xFFF; // pwm.c:31-32
        int period = Math.Max(cycles, 1); // a period of 0 would mean "every elapsed cycle"; floored
                                           // to 1 purely so the loop below always makes progress —
                                           // not a claimed hardware behavior for that extreme edge.
        int guard = 0;
        while (_pwmCycleDebt >= period && guard++ < 4096) // guard is defensive only, never expected to trip
        {
            _pwmCycleDebt -= period;
            for (int channel = 0; channel < 2; channel++)
            {
                if (PwmFifoTryDequeue(channel, out ushort raw))
                {
                    _pwmCurrent[channel] = ConvertSample(raw, cycles);
                }
                // else: hold the previous _pwmCurrent[channel] -- underrun repeats the last
                // sample rather than going silent, confirmed against consume_fifo_do, pwm.c:95-106.
            }

            // One shared counter for both channels together -- see _pwmIrqCounter's own remarks
            // and RaisePwmInterrupt's, confirmed against consume_fifo_do (pwm.c:112-114):
            // "if (--Pico32x.pwm_irq_cnt <= 0) { reload; do_pwm_irq(...); }", called once per
            // period regardless of which channel(s) actually had data queued.
            if (--_pwmIrqCounter <= 0)
            {
                _pwmIrqCounter = ComputePwmIrqReload();
                RaisePwmInterrupt();
            }
        }
    }

    /// <summary>Confirmed against PicoDrive's <c>convert_sample</c> (<c>pwm.c:60-65</c>): the raw
    /// FIFO value is clamped to the current sample period and linearly rescaled into a
    /// PCM-ish range centered on silence — no separate unsigned-to-signed step, this formula
    /// *is* the conversion.</summary>
    private static short ConvertSample(int v, int cycles)
    {
        if (v > cycles)
        {
            v = cycles;
        }

        long mult = (0x10000L << 8) / (cycles + 1);
        long scaled = ((long)v * mult >> 8) - 0x8000;
        return (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
    }

    private bool PwmFifoTryDequeue(int channel, out ushort value)
    {
        if (_pwmFifoCount[channel] == 0)
        {
            value = 0;
            return false;
        }

        value = _pwmFifo[channel][_pwmFifoHead[channel]];
        _pwmFifoHead[channel] = (_pwmFifoHead[channel] + 1) % PwmFifoDepth;
        _pwmFifoCount[channel]--;
        return true;
    }

    private void PwmFifoWrite(int channel, ushort rawValue)
    {
        ushort stored = (ushort)((rawValue - 1) & 0xFFF); // pwm.c:252,263

        if (_pwmFifoCount[channel] >= PwmFifoDepth)
        {
            // Overflow: drop the oldest queued entry to make room, matching PicoDrive's own
            // "discard oldest, keep newest" overflow behavior (pwm.c:244-252). The IRQ-reload
            // locking side effect described there isn't replicated -- PWM interrupts aren't
            // implemented this phase (see this file's known-gaps remarks).
            _pwmFifoHead[channel] = (_pwmFifoHead[channel] + 1) % PwmFifoDepth;
            _pwmFifoCount[channel]--;
        }

        int writeIndex = (_pwmFifoHead[channel] + _pwmFifoCount[channel]) % PwmFifoDepth;
        _pwmFifo[channel][writeIndex] = stored;
        _pwmFifoCount[channel]++;
    }

    /// <summary>Register reads for offsets ≥ 0x30, dispatched from <see cref="Sega32X.cs"/>'s
    /// existing control-register read methods. The FIFO "data" registers are write-only on real
    /// hardware — reads return only FULL/EMPTY status bits, in the high byte only
    /// (<c>pwm.c:186-199</c>); everything else is plain storage.</summary>
    private byte ReadPwmByte(uint offset)
    {
        int channel = offset switch
        {
            0x34 or 0x35 => 0,
            0x36 or 0x37 => 1,
            0x38 or 0x39 => 0, // mono alias reads the same status as the Left channel
            _ => -1,
        };

        if (channel >= 0)
        {
            if ((offset & 1) != 0) // low byte: no status bits live here
            {
                return 0;
            }

            if (_pwmFifoCount[channel] >= PwmFifoDepth)
            {
                return PwmFullBit;
            }

            if (_pwmFifoCount[channel] == 0)
            {
                return PwmEmptyBit;
            }

            return 0;
        }

        return ReadRegByte(offset);
    }

    /// <summary>Register writes for offsets ≥ 0x30, dispatched from <see cref="Sega32X.cs"/>'s
    /// existing control-register write methods. Confirmed asymmetric write permission
    /// (<c>memory.c:538-541,637-639</c>): the 68000 can only ever affect <c>xMd</c> (the control
    /// register's low 4 bits) — RTP (bit 7) and the IRQ-timer nibble (bits 8-11, in the high
    /// byte) are SH-2-exclusive. The odd-address FIFO registers push a value on write
    /// (<c>pwm.c</c>'s own write handlers only act on the low-byte/odd-address write of each
    /// pair) using the just-updated full 16-bit register value.</summary>
    private void WritePwmByte(uint offset, byte value, bool fromSh2)
    {
        switch (offset)
        {
            case 0x30: // control high byte: IRQ-timer nibble, SH-2-exclusive
                if (fromSh2)
                {
                    WriteRegByte(offset, value);
                }

                return;

            case 0x31: // control low byte: xMd (both CPUs) + RTP (SH-2 only)
            {
                byte mask = fromSh2 ? (byte)0x8F : (byte)0x0F;
                byte current = ReadRegByte(offset);
                WriteRegByte(offset, (byte)((current & ~mask) | (value & mask)));
                return;
            }

            case 0x32 or 0x33: // cycle register: both CPUs can write freely
                WriteRegByte(offset, value);
                return;

            case 0x34 or 0x35: // Left/Mono FIFO
                WriteRegByte(offset, value);
                if (offset == 0x35)
                {
                    PwmFifoWrite(0, Regs[PwmLeftIndex]);
                }

                return;

            case 0x36 or 0x37: // Right FIFO
                WriteRegByte(offset, value);
                if (offset == 0x37)
                {
                    PwmFifoWrite(1, Regs[PwmRightIndex]);
                }

                return;

            case 0x38 or 0x39: // Mono FIFO alias -- falls through to the Left channel, pwm.c:253
                WriteRegByte(offset, value);
                if (offset == 0x39)
                {
                    PwmFifoWrite(0, Regs[PwmMonoIndex]);
                }

                return;

            default: // 0x3A-0x3F: reserved, ignored
                return;
        }
    }
}
