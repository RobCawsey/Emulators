namespace GenesisSharp.CpuSh2;

public sealed partial class Sh2
{
    /// <summary>Sets the level currently asserted on the external interrupt-request (IRL) pins,
    /// with the vector to dispatch through when it is taken. Level 0 deasserts.
    ///
    /// This models a <em>level</em>, not a queued request, and the distinction is the whole point:
    /// servicing an IRL interrupt does <b>not</b> clear it. Whatever is driving these pins holds
    /// the level asserted until its own condition goes away, so the handler is expected to tell
    /// that device to stop asserting (on 32X, by writing the per-core interrupt-clear registers);
    /// if it doesn't, the interrupt legitimately fires again after <c>RTE</c> restores SR. That is
    /// real hardware behavior, not a bug to design around. Re-entry <em>during</em> the handler is
    /// prevented separately, by <see cref="ServicePendingInterrupt"/> raising SR's I3-I0 mask to
    /// the serviced level.
    ///
    /// Confirmed against PicoDrive's split in <c>sh2_irq_cb</c> (<c>32x.c:18-31</c>): the internal
    /// on-chip-peripheral path explicitly does <c>sh2->pending_int_irq = 0; // auto-clear</c>,
    /// while the IRL path above it returns <c>64 + sh2->pending_irl / 2</c> and clears nothing.
    /// The external sources actually driving these pins on 32X are held in
    /// <c>Pico32x.sh2irqi[core]</c> and cleared only by explicit register writes
    /// (<c>memory.c:936-952</c>) or by their own condition lapsing.
    ///
    /// An earlier revision of this core exposed a <c>RaiseInterrupt(level, vector)</c> that kept a
    /// single pending request and replaced it only when a higher-priority one arrived. That model
    /// silently <em>dropped</em> a lower-priority source asserted while a higher one was pending,
    /// rather than leaving it asserted underneath — a divergence that became reachable as soon as
    /// the highest-priority 32X source (VRES, level 14) started being raised at all. Which source
    /// wins is now decided by whoever owns the pins (see <c>Sega32X.UpdateInterruptRequestLevels</c>,
    /// which mirrors PicoDrive's own <c>p32x_update_irls</c> priority encoder), not by this
    /// core throwing requests away.
    ///
    /// The vector is supplied by the caller rather than derived here: the SH-2 has no fixed
    /// vector-per-level mapping for external interrupts, so it is a property of the interrupt
    /// controller wired to the pins, which is a 32X-specific concern out of scope for this
    /// core.</summary>
    public void SetInterruptRequestLevel(int level, int vectorNumber)
    {
        if (level is < 0 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        InterruptRequestLevel = level;
        _interruptRequestVector = vectorNumber;
    }

    /// <summary>Requests an interrupt from an <em>on-chip peripheral</em> (SCI, DMAC, the timers) —
    /// the other half of the SH-2's interrupt inputs, and deliberately not the same mechanism as
    /// <see cref="SetInterruptRequestLevel"/>.
    ///
    /// Unlike an external IRL, this is a one-shot request that <b>auto-clears</b> when taken:
    /// PicoDrive's <c>sh2_irq_cb</c> (<c>32x.c:18-31</c>) keeps the two in separate fields and
    /// clears only this one on acknowledge — <c>sh2->pending_int_irq = 0; // auto-clear</c>, its
    /// own comment — while returning an auto-vector for the IRL path without clearing anything.
    /// The asymmetry is real hardware: an on-chip peripheral latches its request and the CPU
    /// consumes it, whereas an external device holds a level on the pins until it decides to stop.
    ///
    /// Also unlike the external path, the vector here is genuinely the peripheral's own — supplied
    /// from its vector-number register (e.g. the SCI's VCR block), not derived from the level.
    /// Higher level wins against a concurrently-asserted IRL, matching PicoDrive's own
    /// <c>pending_irl &gt; pending_int_irq</c> comparison.</summary>
    public void RaiseInternalInterrupt(int level, int vectorNumber)
    {
        if (level is < 1 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (level > InternalInterruptLevel)
        {
            InternalInterruptLevel = level;
            _internalInterruptVector = vectorNumber;
        }
    }

    /// <summary>NMI — edge-triggered, always taken regardless of the interrupt mask, modeled as
    /// a one-shot latch (same shape as Z80.RaiseNonMaskableInterrupt).
    ///
    /// Confidence note, explicitly: PicoDrive's SH-2 interpreter does not model NMI at all —
    /// there is no NMI pin, vector, or handling code anywhere in its cpu/sh2 tree (the only hit
    /// is a stray changelog comment mentioning it was "put back" at some point). That could mean
    /// real 32X software doesn't depend on SH-2 NMI, or that the pin isn't usefully wired on 32X
    /// hardware. This stub (and the vector number ServicePendingInterrupt uses for it) is
    /// therefore an unverified best guess, not a citation — included cheaply in case some game
    /// does rely on it, but treat it as the least-confirmed piece of this entire core.</summary>
    public void RaiseNonMaskableInterrupt() => NmiPending = true;

    /// <summary>Services NMI (unconditionally, if pending) or the asserted IRL level (only if it
    /// exceeds SR's I3-I0 mask), NMI taking priority. Both push SR then PC (confirmed push order
    /// against PicoDrive's sh2_do_irq, cpu/sh2/sh2.c:56-59), raise the interrupt mask to the
    /// serviced level for IRL interrupts (same citation), and fetch the handler address from
    /// VBR + vector*4. Returns 0 if nothing was serviced.
    ///
    /// Note what is deliberately absent: the IRL branch does not clear
    /// <see cref="InterruptRequestLevel"/>. Raising the mask to the serviced level is what stops
    /// it re-entering before <c>RTE</c>; after that, it re-fires unless the source stopped
    /// asserting. See <see cref="SetInterruptRequestLevel"/>.</summary>
    private int ServicePendingInterrupt()
    {
        if (NmiPending)
        {
            NmiPending = false;
            PushLong(SR);
            PushLong(PC);
            PC = _bus.ReadLong(VBR + 11u * 4); // vector 11: unverified, see RaiseNonMaskableInterrupt
            return 13; // unverified — no PicoDrive citation for NMI cost specifically
        }

        // Whichever input is asserting the higher level wins, matching PicoDrive's own
        // `pending_irl > pending_int_irq` comparison (32x.c:20). Only the winner's own clearing
        // rule applies -- an internal request that loses this comparison stays latched.
        bool internalWins = InternalInterruptLevel > InterruptRequestLevel;
        int level = internalWins ? InternalInterruptLevel : InterruptRequestLevel;
        int vector = internalWins ? _internalInterruptVector : _interruptRequestVector;

        if (level > 0 && level > InterruptMask)
        {
            if (internalWins)
            {
                InternalInterruptLevel = 0; // auto-clear; the external path deliberately does not
                _internalInterruptVector = 0;
            }

            PushLong(SR);
            PushLong(PC);
            InterruptMask = level;
            PC = _bus.ReadLong(VBR + (uint)vector * 4);

            // "13 cycles at best" — PicoDrive's own comment, cpu/sh2/sh2.c:67; adopted as-is
            // rather than a more precise figure PicoDrive itself doesn't claim to have either.
            return 13;
        }

        return 0;
    }
}
