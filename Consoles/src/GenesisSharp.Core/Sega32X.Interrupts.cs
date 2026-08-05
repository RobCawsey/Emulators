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
    // VRES/HINT/PWM get their own constants (14/71, 10/69, 6/67 respectively) in the later phases
    // that actually raise them.
    private const int CmdLevel = 8;
    private const int CmdVector = 68;
    private const int VIntLevel = 12;
    private const int VIntVector = 70;

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

    /// <summary>Re-evaluates the CMD interrupt condition for one core and requests it if both the
    /// 68000's own request bit (adapter-block offset 2/3 -- <c>Regs[1]</c>'s low byte, bit 0 =
    /// request-to-master, bit 1 = request-to-slave) and that core's own <see cref="CmdMaskBit"/>
    /// are set. A live AND, not a one-shot latch -- confirmed against PicoDrive's own
    /// <c>p32x_update_cmd_irq</c> (<c>32x.c:89-102</c>), which is called from both the 68000's
    /// own offset-3 write and the SH-2's own mask-register write for exactly this reason: either
    /// side changing can flip the outcome. Safe to call unconditionally on every relevant write:
    /// <c>Sh2.RaiseInterrupt</c> itself only actually raises the pending level if the new one is
    /// higher than whatever's already pending, so re-evaluating an already-true condition is a
    /// harmless no-op rather than something that needs its own guard here.</summary>
    private void UpdateCmdIrq(int core)
    {
        bool masked = (Sh2IrqMask[core] & CmdMaskBit) != 0;
        bool requested = (Regs[1] & (1 << core)) != 0;
        if (masked && requested)
        {
            (core == 0 ? MasterSh2 : SlaveSh2).RaiseInterrupt(CmdLevel, CmdVector);
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
                (core == 0 ? MasterSh2 : SlaveSh2).RaiseInterrupt(VIntLevel, VIntVector);
            }
        }
    }
}
