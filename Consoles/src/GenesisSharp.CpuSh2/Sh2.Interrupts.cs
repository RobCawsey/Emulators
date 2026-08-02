namespace GenesisSharp.CpuSh2;

public sealed partial class Sh2
{
    /// <summary>Requests a maskable interrupt at the given priority (1-15) and vector number.
    /// Only raises if higher than whatever's already pending — same priority-encoder reasoning
    /// as M68000.RaiseInterrupt, adapted to the SH-2's wider 4-bit mask. Unlike the 68000's
    /// autovectored scheme, the SH-2 has no fixed vector-per-level mapping — external interrupt
    /// vector numbers are configurable by whatever's wiring the interrupt controller, a
    /// 32X-specific concern out of scope for this core, so the caller supplies one.</summary>
    public void RaiseInterrupt(int level, int vectorNumber)
    {
        if (level is < 1 or > 15)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }

        if (level > PendingInterruptLevel)
        {
            PendingInterruptLevel = level;
            _pendingVectorNumber = vectorNumber;
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

    /// <summary>Services NMI (unconditionally, if pending) or the pending maskable interrupt
    /// (only if its level exceeds SR's I3-I0 mask), NMI taking priority. Both push SR then PC
    /// (confirmed push order against PicoDrive's sh2_do_irq, cpu/sh2/sh2.c:56-59), raise the
    /// interrupt mask to the serviced level for maskable interrupts (same citation), and fetch
    /// the handler address from VBR + vector*4. Returns 0 if nothing was serviced.</summary>
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

        if (PendingInterruptLevel > 0 && PendingInterruptLevel > InterruptMask)
        {
            int level = PendingInterruptLevel;
            int vector = _pendingVectorNumber;
            PendingInterruptLevel = 0;

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
