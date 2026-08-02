namespace GenesisSharp.CpuSh2;

/// <summary>Hitachi SH-2 core — one of the 32X's two application CPUs (master/slave are the same
/// core, constructed twice; which one a given instance is has no bearing on instruction
/// semantics and is entirely a bus/console-side concern). A general-purpose core with no
/// 32X-specific knowledge, exactly like M68000/Z80 have no Genesis-specific knowledge.
///
/// Confidence note: this core was built against, and is cited throughout against,
/// <c>reference/PicoDrive/picodrive</c>'s SH-2 interpreter (<c>cpu/sh2/sh2.c</c> and
/// <c>cpu/sh2/mame/sh2.c</c>) — a real, working 32X-capable emulator, not general recollection.
/// Where this core's design deliberately diverges from PicoDrive (see <see cref="Step"/>'s doc
/// comment on delay slots) or where PicoDrive itself doesn't resolve something (NMI — see
/// <see cref="RaiseNonMaskableInterrupt"/>), that's flagged explicitly rather than silently
/// assumed. See ARCHITECTURE.md for the full citation trail.</summary>
public sealed partial class Sh2
{
    private readonly IBus _bus;

    /// <summary>R0-R15. R0 is architecturally special in a handful of indexed-addressing forms
    /// (e.g. <c>MOV.B @(R0,Rn)</c>); R15 is the conventional stack pointer, used implicitly by
    /// exception entry (SR/PC pushed via predecrement, confirmed against PicoDrive's
    /// <c>sh2_do_irq</c>, <c>cpu/sh2/sh2.c:56-59</c>) — but unlike 68000's A7, R15 is not
    /// otherwise distinguished by the instruction set; ordinary code is free to use it as a
    /// plain register.</summary>
    public uint[] R { get; } = new uint[16];

    public uint PC { get; set; }

    /// <summary>Procedure register — return address for BSR/BSRF/JSR, and the target RTS jumps
    /// to.</summary>
    public uint PR { get; set; }

    /// <summary>Global base register — base for GBR-relative addressing and the GBR-relative
    /// logic-immediate instructions (AND.B/OR.B/XOR.B/TST.B against @(R0,GBR)).</summary>
    public uint GBR { get; set; }

    /// <summary>Vector base register — base for exception vector fetch (see
    /// <see cref="ServicePendingInterrupt"/>). Unlike the 68000's fixed autovector table, the
    /// SH-2's vector table location is configurable; confirmed zeroed on reset
    /// (<see cref="Reset"/>).</summary>
    public uint VBR { get; set; }

    /// <summary>Multiply-accumulate result, high/low 32 bits — written by MAC.W/MAC.L/MUL family
    /// instructions (<c>Sh2.MacMultiply.cs</c>).</summary>
    public uint MACH { get; set; }
    public uint MACL { get; set; }

    /// <summary>Status register. Bit layout confirmed against PicoDrive's <c>cpu/sh2/mame/
    /// sh2pico.c:64-72</c>: bit 0 = T (test/true — condition flag read by BT/BF, written by
    /// CMP/TST/DT/etc.), bit 1 = S (saturation mode for MAC), bits 4-7 = I3-I0 (interrupt
    /// priority mask, 0-15), bit 8 = Q, bit 9 = M (Q/M used only by the DIV0S/DIV1 iterative-
    /// division algorithm — <c>Sh2.Divide.cs</c>). All other bits reserved, read as 0. See
    /// <see cref="Sh2.Flags.cs"/> for the named bit accessors.</summary>
    public uint SR { get; set; }

    public long TotalCycles { get; private set; }

    /// <summary>0 = no maskable interrupt pending. 1-15 mirrors the SH-2's external IRL
    /// priority levels, compared against SR's I3-I0 mask — see
    /// <see cref="RaiseInterrupt"/>.</summary>
    public int PendingInterruptLevel { get; private set; }

    private int _pendingVectorNumber;

    /// <summary>One-shot NMI latch — see <see cref="RaiseNonMaskableInterrupt"/>'s doc comment
    /// for why this is a best-effort stub, not a confirmed-against-PicoDrive fact the way the
    /// rest of this core's interrupt handling is.</summary>
    public bool NmiPending { get; private set; }

    /// <summary>Set mid-<see cref="Step"/> while executing a delay-slot instruction, so nested
    /// helpers (illegal-slot-instruction detection) know they're already inside one — never
    /// observably true between <see cref="Step"/> calls, so it is deliberately not part of
    /// <see cref="SaveState"/>. See <see cref="Step"/>'s doc comment for the full design
    /// rationale.</summary>
    private bool _inDelaySlot;

    public Sh2(IBus bus)
    {
        _bus = bus;
    }

    /// <summary>Confirmed against PicoDrive's <c>sh2_reset</c> (<c>cpu/sh2/sh2.c:43-50</c>): PC
    /// and R15 are loaded from fixed physical addresses 0 and 4 (mirroring how the 68000 core
    /// reads its own reset vector), VBR is explicitly zeroed, and SR is set to just the I3-I0
    /// mask fully set (0xF0) — i.e. every maskable interrupt starts out blocked until software
    /// lowers the mask itself.</summary>
    public void Reset()
    {
        Array.Clear(R);
        PC = _bus.ReadLong(0x0000_0000);
        R[15] = _bus.ReadLong(0x0000_0004);
        SR = 0x0000_00F0;
        VBR = 0;
        GBR = 0;
        PR = 0;
        MACH = 0;
        MACL = 0;
        TotalCycles = 0;
        PendingInterruptLevel = 0;
        _pendingVectorNumber = 0;
        NmiPending = false;
        _inDelaySlot = false;
    }

    /// <summary>Executes one instruction and returns its clock-cycle cost. Interrupt delivery is
    /// checked first, exactly like the 68000/Z80 cores check theirs at the start of every
    /// <see cref="Step"/>.
    ///
    /// Delayed branches (BRA/BSR/BRAF/BSRF/JMP/JSR/RTS/RTE/BT.S/BF.S — see
    /// <c>Sh2.ControlFlow.cs</c>) are handled *within* this same call: the branch instruction's
    /// handler fetches and executes its mandatory delay-slot instruction itself before this
    /// method returns, applying the branch's PC update only afterward, and the combined cycle
    /// cost of both instructions is returned as one number. This keeps the same "Step() is
    /// atomic between calls" contract the 68000 core's save-state design already relies on.
    ///
    /// This is a deliberate adaptation, not a literal port: PicoDrive's own interpreter isn't
    /// built around a single-instruction-per-call API (its <c>sh2_execute_interpreter</c> runs a
    /// whole timeslice per call), so its "delay" field is just internal bookkeeping within one
    /// big loop, not something that has to survive an external call boundary the way this core's
    /// design requires. Confirmed from that source, and preserved here: interrupts are never
    /// sampled between a delayed branch and its delay-slot instruction (PicoDrive's own loop
    /// comment: "can't interrupt before delay", <c>cpu/sh2/mame/sh2pico.c:183</c>) — this core
    /// gets that property for free, since there is no <see cref="Step"/> call boundary between
    /// them at all.</summary>
    public int Step()
    {
        int interruptCycles = ServicePendingInterrupt();
        if (interruptCycles > 0)
        {
            TotalCycles += interruptCycles;
            return interruptCycles;
        }

        ushort opcode = FetchWord();
        int cycles = Execute(opcode);
        TotalCycles += cycles;
        return cycles;
    }

    private ushort FetchWord()
    {
        ushort value = _bus.ReadWord(PC);
        PC += 2;
        return value;
    }

    private void PushLong(uint value)
    {
        R[15] -= 4;
        _bus.WriteLong(R[15], value);
    }

    private uint PopLong()
    {
        uint value = _bus.ReadLong(R[15]);
        R[15] += 4;
        return value;
    }
}
