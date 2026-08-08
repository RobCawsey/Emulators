namespace GenesisSharp.Core;

/// <summary>Phases 1-2 of the in-progress 32X interrupt-routing extension (see
/// <c>32X-INTERRUPT-ROUTING-PLAN.md</c>): the CMD interrupt (68000 → SH-2), the SH-2's own
/// per-core interrupt-enable register, and VINT (the 68000's own VDP vblank → SH-2). HINT's own
/// scheduling is deliberately deferred, not merely unstarted — PicoDrive's own source calls it
/// "rather rough... useless in practice" (<c>32x.c:350</c>), and it needs a genuinely separate
/// timer (driven by its own SH-2-side "H count" register, adapter offset 4/5, independent of the
/// 68000's own VDP register 10) rather than a simple hook into an existing event, unlike VINT.
/// VRES/PWM are deferred to their own later phases of that same plan.
///
/// Ground truth: <c>reference/PicoDrive/picodrive/pico/32x/32x.c</c> and
/// <c>pico/32x/memory.c</c>, cited per-member below. All five 32X interrupt sources are SH-2-side
/// only — nothing in PicoDrive's 32X code raises an interrupt on the 68000 itself from a 32X
/// event; the 68000 is notified of SH-2 activity by polling COMM registers, not by receiving its
/// own interrupt (confirmed by an exhaustive search of <c>32x.c</c>/<c>memory.c</c>/<c>pwm.c</c>
/// for any 68000-facing interrupt call site — there is none).</summary>
public sealed partial class Sega32X
{
    // Bit positions within Sh2IrqMask[core] -- the SH-2's own view of adapter-block offset 1
    // ("HEN/irq masks"), confirmed against PicoDrive's p32x_sh2reg_write8 (memory.c:824-839) and
    // its mask-to-pending-bit shift (32x.c:83-84: `mask & (sh2irq_mask[core] << 3)` lines up
    // exactly against P32XI_PWM/CMD/HINT/VINT's own bit positions, pico_int.h:624-628). This is
    // completely distinct from the 68000's own meaning of the same byte offset (nRES/ADEN,
    // already correctly handled in WriteControlByteFrom68k) -- not a rename of the same bits, a
    // genuinely different register depending on which CPU is asking.
    private const byte PwmMaskBit = 1 << 0;
    private const byte CmdMaskBit = 1 << 1;
    private const byte HIntMaskBit = 1 << 2;
    private const byte VIntMaskBit = 1 << 3;

    // (level, vector) confirmed by hand-tracing p32x_update_irls's priority encoder (32x.c:34-74)
    // against pico_int.h:624-628's bit positions, cross-checked against sh2_irq_cb's own
    // auto-vector formula (32x.c:18-31: "return 64 + pending_irl / 2" -- the real SH-2 hardware
    // convention for an auto-vectored external interrupt with no explicit vector asserted).
    // Pending-source bits, one set per core, matching PicoDrive's own P32XI_* bit positions
    // (pico_int.h:624-628) exactly -- the positions are load-bearing, not arbitrary labels, since
    // both the level/vector derivation in UpdateInterruptRequestLevels and the mask alignment in
    // the raise paths below depend on them (32x.c:83-84 shifts sh2irq_mask left by 3 to line its
    // PWM/CMD/HINT/VINT enable bits up against these).
    private const byte VResPendingBit = 0x80;
    private const byte VIntPendingBit = 0x40;
    private const byte HIntPendingBit = 0x20;
    private const byte CmdPendingBit = 0x10;
    private const byte PwmPendingBit = 0x08;

    /// <summary>Which of the five 32X sources are currently <em>asserting</em> an interrupt on each
    /// core, index 0 = master. This is the direct equivalent of PicoDrive's
    /// <c>Pico32x.sh2irqi[2]</c> (<c>pico_int.h:651</c>), and it lives here rather than inside the
    /// SH-2 core for the same reason it does there: these are external sources wired to the CPU's
    /// interrupt-request pins, so which one wins is the interrupt controller's job, not the CPU's.
    ///
    /// Level-triggered. A bit stays set until its source stops asserting — for VRES/VINT/HINT/PWM
    /// that means the handler writing this core's own interrupt-clear register
    /// (<see cref="ClearPendingInterrupt"/>), and for CMD it means the live AND in
    /// <see cref="UpdateCmdIrq"/> going false. Servicing the interrupt does <em>not</em> clear it;
    /// see <see cref="Sh2.SetInterruptRequestLevel"/>.</summary>
    internal readonly byte[] Sh2IrqPending = new byte[2];

    /// <summary>The SH-2's own per-core interrupt-enable register -- adapter-block offset 1
    /// written from the SH-2 side, a completely different meaning from the 68000's own view of
    /// the same offset (nRES/ADEN). Index 0 = master, index 1 = slave. Only the low 4 bits are
    /// meaningful (<see cref="PwmMaskBit"/>/<see cref="CmdMaskBit"/>/<see cref="HIntMaskBit"/>/
    /// <see cref="VIntMaskBit"/>); VRES is never maskable via this register at all (applied
    /// unconditionally on both cores, confirmed <c>32x.c:81-82</c>, outside the masked
    /// expression the other four sources go through). Confirmed against PicoDrive's
    /// <c>p32x_sh2reg_write8</c>, <c>memory.c:824-839</c>. Public (not internal) for direct test
    /// inspection, matching how <see cref="Regs"/>/<see cref="VdpRegs"/>/<see cref="Sdram"/> are
    /// already exposed.</summary>
    public readonly byte[] Sh2IrqMask = new byte[2];

    /// <summary>Recomputes both cores' asserted interrupt level from <see cref="Sh2IrqPending"/>
    /// and drives it onto their request pins. The direct equivalent of PicoDrive's
    /// <c>p32x_update_irls</c> (<c>32x.c:34-74</c>), which does the same job with an unrolled
    /// binary search for the highest set bit; the levels and vectors this produces were confirmed
    /// identical to that encoder's for all five sources.
    ///
    /// Both derivations fall out of the bit positions above. The level is <c>2 × bitIndex</c>
    /// (VRES at bit 7 → 14, VINT bit 6 → 12, HINT bit 5 → 10, CMD bit 4 → 8, PWM bit 3 → 6), and
    /// the vector is <c>64 + bitIndex</c>, which is exactly PicoDrive's
    /// <c>64 + pending_irl / 2</c> (<c>sh2_irq_cb</c>, <c>32x.c:22</c>) — the SH-2 hardware
    /// convention for auto-vectored external interrupts, where no vector is explicitly asserted on
    /// the bus. Nothing pending deasserts the pins entirely (level 0).</summary>
    private void UpdateInterruptRequestLevels()
    {
        for (int core = 0; core < 2; core++)
        {
            byte pending = Sh2IrqPending[core];
            int level = 0;
            int vector = 0;
            if (pending != 0)
            {
                int bit = System.Numerics.BitOperations.Log2(pending); // highest set bit
                level = bit * 2;
                vector = 64 + bit;
            }

            (core == 0 ? MasterSh2 : SlaveSh2).SetInterruptRequestLevel(level, vector);
        }
    }

    /// <summary>Asserts one source on one core. Idempotent — re-asserting an already-asserted
    /// source is a harmless no-op, which is what lets the live-AND and per-scanline callers below
    /// run unconditionally without their own guards.</summary>
    private void AssertPendingInterrupt(int core, byte bit)
    {
        Sh2IrqPending[core] |= bit;
        UpdateInterruptRequestLevels();
    }

    /// <summary>Deasserts one source on one core — the mechanism a real interrupt handler uses to
    /// stop an interrupt re-firing, since servicing alone never clears it. Reached from the
    /// per-core interrupt-clear register block (adapter offsets 0x14/0x16/0x18/0x1c for
    /// VRES/VINT/HINT/PWM), confirmed against PicoDrive's own <c>p32x_sh2reg_write16</c>
    /// (<c>memory.c:936-952</c>). CMD (0x1a) deliberately isn't routed here: it clears the
    /// 68000's request bit instead and lets <see cref="UpdateCmdIrq"/>'s live AND fall false, the
    /// same split PicoDrive makes.</summary>
    private void ClearPendingInterrupt(int core, byte bit)
    {
        Sh2IrqPending[core] &= (byte)~bit;
        UpdateInterruptRequestLevels();
    }

    /// <summary>Re-evaluates the CMD interrupt condition for one core and requests it if both the
    /// 68000's own request bit (adapter-block offset 2/3 -- <c>Regs[1]</c>'s low byte, bit 0 =
    /// request-to-master, bit 1 = request-to-slave) and that core's own <see cref="CmdMaskBit"/>
    /// are set. A live AND, not a one-shot latch -- confirmed against PicoDrive's own
    /// <c>p32x_update_cmd_irq</c> (<c>32x.c:89-102</c>), which is called from both the 68000's
    /// own offset-3 write and the SH-2's own mask-register write for exactly this reason: either
    /// side changing can flip the outcome. Safe to call unconditionally on every relevant write.
    ///
    /// Genuinely bidirectional, unlike the other four sources: this both asserts and <em>deasserts</em>
    /// CMD, matching PicoDrive's own if/else (<c>32x.c:91-100</c>, <c>sh2irqi[n] |= P32XI_CMD</c>
    /// vs <c>&amp;= ~P32XI_CMD</c>). That is why CMD has no entry in the interrupt-clear register
    /// block a handler would otherwise use — writing offset 0x1a clears the 68000's request bit and
    /// lets this live AND fall false on its own (see <see cref="AcknowledgeCmdIrq"/>).</summary>
    private void UpdateCmdIrq(int core)
    {
        bool masked = (Sh2IrqMask[core] & CmdMaskBit) != 0;
        bool requested = (Regs[1] & (1 << core)) != 0;
        if (masked && requested)
        {
            AssertPendingInterrupt(core, CmdPendingBit);
        }
        else
        {
            ClearPendingInterrupt(core, CmdPendingBit);
        }
    }

    /// <summary>The SH-2's own acknowledgment of a serviced CMD interrupt: clears its own request
    /// bit in the 68000-shared offset-2/3 register and re-evaluates the CMD condition (which,
    /// with the request bit just cleared, normally goes false -- unless the 68000 re-requests it
    /// again in the interim, the same live-AND semantics <see cref="UpdateCmdIrq"/> already
    /// relies on). Confirmed against PicoDrive's own pending-interrupt-clear register block: a
    /// real SH-2's interrupt handler writes here as its first action, to a *different* offset
    /// (0x1a) than the one the 68000 itself writes to request it (offset 2/3) -- not a symmetric
    /// "write the same register back" acknowledgment (<c>memory.c:936-948</c>, <c>case
    /// 0x1a/2</c> within the word-write path). There is no byte-write equivalent on real
    /// hardware at all — confirmed by <c>p32x_sh2reg_write8</c> having no case for this offset,
    /// falling through to its own "unhandled sysreg" log line — real SH-2 code always uses a word
    /// write here. GenesisSharp's own <c>Sega32XSh2Bus.WriteWord</c> decomposes that into two
    /// separate <c>WriteByte</c> calls, hence <see cref="WriteRegisterByteFromSh2"/> matching on
    /// <c>offset &amp; ~1</c> to treat both bytes of the word-pair as the same register — the
    /// same word-aligned masking PicoDrive's own <c>a &amp;= 0x3e</c> does before its own
    /// switch.</summary>
    private void AcknowledgeCmdIrq(int core)
    {
        Regs[1] &= (ushort)~(1 << core);
        UpdateCmdIrq(core);
    }

    /// <summary>Wired to <see cref="Vdp.EnteredVBlank"/> by <see cref="GenesisConsole"/>'s
    /// constructor — deliberately *not* <see cref="Vdp.VerticalBlankStarted"/> (the 68000/Z80's
    /// own vblank interrupt hook), since that one is gated by <see
    /// cref="Vdp.VerticalInterruptEnabled"/> and 32X VINT isn't: confirmed against PicoDrive's own
    /// <c>p32x_start_blank</c> (<c>32x.c:316-330</c>), which raises VINT unconditionally at
    /// exactly this edge with no dependency on whether the 68000 wants its own vblank interrupt at
    /// all. Found the hard way — an earlier revision hooked the gated event, which silently never
    /// fired in any test/ROM that doesn't happen to also enable the 68000's own VDP interrupt.
    /// Unlike <see cref="UpdateCmdIrq"/>, there's no persistent "still requested" register state
    /// to re-evaluate here — VINT is a one-shot edge, not a live AND — so this simply raises it
    /// once per core, gated only by that core's own <see cref="VIntMaskBit"/>.</summary>
    public void OnVerticalBlankStarted()
    {
        for (int core = 0; core < 2; core++)
        {
            if ((Sh2IrqMask[core] & VIntMaskBit) != 0)
            {
                AssertPendingInterrupt(core, VIntPendingBit);
            }
        }
    }

    /// <summary>Called from <see cref="Sega32X.Pwm.cs"/>'s own sample-consumption loop once every
    /// (IRQ-timer-nibble + 1) samples actually dequeued -- confirmed against PicoDrive's own
    /// <c>do_pwm_irq</c> (<c>pwm.c:49-58</c>) and its caller <c>consume_fifo_do</c>
    /// (<c>pwm.c:112-114</c>, <c>--Pico32x.pwm_irq_cnt &lt;= 0</c>). This is PWM's real
    /// "FIFO needs feeding" signal real 32X software depends on to know *when* to push the next
    /// batch of samples — without it, a game has no way to pace its own FIFO writes against the
    /// PWM chip's actual consumption rate, and typically either pushes too little (audible
    /// underrun/repeated-sample garbling — this core already reproduces underrun-holds-last-value
    /// correctly, see <see cref="Sega32X.Pwm.AdvancePwmClock"/>'s own remarks, but only once
    /// *something* tells it a new sample is due) or too much (overflow, silently dropping queued
    /// audio, <see cref="Sega32X.Pwm.PwmFifoWrite"/>'s own remarks). RTP's own auto-DREQ1-trigger
    /// side effect (<c>pwm.c:53-57</c>) is a real, separate, still-unimplemented gap (external/
    /// DREQ-driven DMA transfers aren't modeled at all yet — see <see
    /// cref="Sega32XSh2Bus.TryTriggerDmaChannel"/>'s own remarks on that same boundary) — named,
    /// not silently pretended-correct.</summary>
    internal void RaisePwmInterrupt()
    {
        for (int core = 0; core < 2; core++)
        {
            if ((Sh2IrqMask[core] & PwmMaskBit) != 0)
            {
                AssertPendingInterrupt(core, PwmPendingBit);
            }
        }

        // RTP: the same event also asserts the PWM chip's DMA request line, letting a DMAC channel
        // refill the FIFO without the SH-2 writing each sample by hand. Confirmed against
        // PicoDrive's do_pwm_irq (pwm.c:49-58), where the DREQ1 trigger sits alongside the
        // interrupt under exactly this bit. Offered to both cores because either may have armed a
        // channel for it; each ignores it unless its own channel 1 is enabled and unfinished (see
        // Sega32XSh2Bus.TriggerDreq1).
        //
        // Note this is the interrupt *and* the DMA request, not one or the other: software can
        // legitimately mask the PWM interrupt off on both cores and still rely on RTP to feed the
        // FIFO, which is why this sits outside the per-core mask loop above.
        if ((Regs[PwmControlIndex] & PwmRtpBit) != 0)
        {
            _masterSh2Bus.TriggerDreq1();
            _slaveSh2Bus.TriggerDreq1();
        }
    }

    /// <summary>The highest-priority 32X interrupt (level 14, the top of the five-source table
    /// above), raised on <em>both</em> cores unconditionally — <see cref="Sh2IrqMask"/> does not
    /// gate it. Confirmed against PicoDrive's <c>p32x_trigger_irq</c> (<c>32x.c:79-88</c>), where
    /// VRES is applied in its own two statements <em>before</em>, and textually outside, the masked
    /// expression the other four sources go through:
    /// <code>
    /// Pico32x.sh2irqi[0] |= mask &amp; P32XI_VRES;
    /// Pico32x.sh2irqi[1] |= mask &amp; P32XI_VRES;
    /// Pico32x.sh2irqi[0] |= mask &amp; (Pico32x.sh2irq_mask[0] &lt;&lt; 3);
    /// Pico32x.sh2irqi[1] |= mask &amp; (Pico32x.sh2irq_mask[1] &lt;&lt; 3);
    /// </code>
    ///
    /// Despite the name, this is <em>not</em> the SH-2 reset line and has nothing to do with the
    /// adapter's <c>nRES</c> bit. The two are genuinely separate mechanisms on real hardware, and
    /// conflating them is the obvious mistake here: <c>p32x_reset_sh2s</c> (<c>32x.c:161-212</c>),
    /// the function tied to the software <c>nRES</c> 0→1 register edge that
    /// <see cref="WriteControlByteFrom68k"/> already mirrors, never raises VRES anywhere in its
    /// body. VRES-the-interrupt comes solely from <c>PicoReset32x</c> (<c>32x.c:240-249</c>), the
    /// whole-system reset path — which is why the only call site here is
    /// <see cref="Sega32X.Reset"/>, reached only from <see cref="GenesisConsole.Reset"/>, and
    /// deliberately not the <c>nRES</c> edge.
    ///
    /// Call ordering matters and is not incidental: <see cref="Sh2.Reset"/> clears
    /// <c>PendingInterruptLevel</c>, so raising this before the cores are reset would leave no
    /// trace of it. <see cref="Sega32X.Reset"/> therefore calls this last, after both cores have
    /// been reset and their boot state synthesized.</summary>
    internal void RaiseVResInterrupt()
    {
        AssertPendingInterrupt(0, VResPendingBit);
        AssertPendingInterrupt(1, VResPendingBit);
    }

    /// <summary>The SH-2-exclusive "H count" register (adapter-block offset 5 -- see the write
    /// side's own remarks in <see cref="Sega32X.WriteRegisterByteFromSh2"/> for why this is
    /// separate storage from the 68000's own offset-5 ROM-bank-select register, despite sharing
    /// the same byte offset). HINT fires every (this value + 1) scanlines during active display,
    /// confirmed against PicoDrive's own scheduling formula: <c>hint_counter += (sh2_regs[4/2] +
    /// 1) * 488.5-ish-cycles</c> (<c>32x.c:357</c>) -- i.e. one scanline's worth of cycles per
    /// increment of this register, the exact shape <see cref="AdvanceHIntCountdown"/> reproduces
    /// with a plain per-scanline integer countdown instead of PicoDrive's cycle-precise
    /// fixed-point event scheduler, the same adaptation <see cref="Vdp.Timing.cs"/>'s own
    /// <c>_hInterruptCountdown</c> (the 68000/Z80 side's H-interrupt) already makes for the base
    /// Genesis VDP.</summary>
    private byte _hIntCounterReg;

    /// <summary>Scanlines remaining until the next HINT fire -- shared between both cores (real
    /// hardware has exactly one H-count register and one counter, not a per-core pair; both SH-2s
    /// listening for HINT get interrupted off the same schedule, confirmed <c>32x.c:348-364</c>
    /// operating on a single <c>Pico32x.hint_counter</c> with no per-core split).</summary>
    private int _hIntCountdown;

    /// <summary>Called once per scanline by <c>GenesisConsole.RunScanline</c>, mirroring how <see
    /// cref="Vdp.AdvanceScanline"/> ticks the base Genesis H-interrupt -- decrements the shared
    /// countdown during active display only (real hardware's HINT is scanline-paced, confirmed by
    /// the cycles-per-increment formula in <see cref="_hIntCounterReg"/>'s own remarks; blanking
    /// lines don't tick it, the same "active display only" simplification the base Genesis
    /// H-interrupt already makes) and raises HINT on whichever core(s) have <see
    /// cref="HIntMaskBit"/> set once it goes negative, then reloads from <see
    /// cref="_hIntCounterReg"/>.
    ///
    /// Deliberately does *not* attempt to replicate <c>p32x_schedule_hint</c>'s own "bit 0x80"
    /// (adapter offset 1's top bit, alongside the per-core IRQ mask living in that same byte's low
    /// nibble) gating condition -- PicoDrive's own source calls the whole HINT mechanism "rather
    /// rough, useless in practice" (<c>32x.c:350</c>), and that bit's exact real-hardware meaning
    /// (it interacts with the base Genesis VDP's own vblank-phase status flag in a way not fully
    /// pinned down by this project's own research pass) risks introducing an incorrectly-polarized
    /// gate that silently suppresses HINT entirely -- a strictly worse failure mode than this
    /// simpler "always tick while any core's mask wants it" version, which degrades gracefully
    /// (inert for any ROM that never sets <see cref="HIntMaskBit"/>, the same safety shape as
    /// every other still-simplified area of this class).</summary>
    public void AdvanceHIntCountdown(bool isActiveDisplay)
    {
        bool anyCoreWantsHInt = (Sh2IrqMask[0] & HIntMaskBit) != 0 || (Sh2IrqMask[1] & HIntMaskBit) != 0;
        if (!isActiveDisplay || !anyCoreWantsHInt)
        {
            return;
        }

        _hIntCountdown--;
        if (_hIntCountdown < 0)
        {
            _hIntCountdown = _hIntCounterReg;
            for (int core = 0; core < 2; core++)
            {
                if ((Sh2IrqMask[core] & HIntMaskBit) != 0)
                {
                    AssertPendingInterrupt(core, HIntPendingBit);
                }
            }
        }
    }

    /// <summary>Called from <see cref="UpdateBlankingState"/> on the vblank-exit edge, matching
    /// real hardware's own <c>p32x_end_blank</c> (<c>32x.c:332-346</c>), which likewise
    /// (re)schedules HINT right as active display resumes -- "min 4 SH-2 cycles to pass Mars
    /// Check" per its own comment, i.e. real hardware wants the *first* post-vblank HINT to arrive
    /// almost immediately rather than a full <see cref="_hIntCounterReg"/>-scanlines later.
    /// Resetting to 0 (fires on this scanline's own <see cref="AdvanceHIntCountdown"/> call, the
    /// very next tick) reproduces that "near-immediate first fire, then every (H+1) lines
    /// thereafter" shape without needing this project's own fixed-point sub-scanline precision.</summary>
    private void ResetHIntCountdown() => _hIntCountdown = 0;

    private void ResetHInt()
    {
        _hIntCounterReg = 0;
        _hIntCountdown = 0;
    }
}
