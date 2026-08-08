namespace GenesisSharp.CpuSh2;

/// <summary>Branches. Confirmed line-for-line against PicoDrive's cpu/sh2/mame/sh2.c (per-method
/// citations) for each instruction's *own* effect (target computation, PR/SR updates, own cycle
/// cost); the shared delay-slot mechanics in <see cref="ExecuteDelayedBranch"/> are this core's
/// own adaptation of the pattern confirmed in cpu/sh2/mame/sh2pico.c's main loop — see
/// <see cref="Sh2.Step"/>'s doc comment for why that adaptation was necessary and what it
/// preserves.
///
/// The single easiest mistake in this file: BT/BF are plain conditional branches (target takes
/// effect immediately, no delay slot); BT.S/BF.S are delayed, and — confirmed against PicoDrive's
/// BTS/BFS (cpu/sh2/mame/sh2.c:351-362,443-454) — *always* consume the delay-slot instruction
/// even when the branch itself isn't taken. Getting that backwards silently breaks any loop
/// using the .S forms.</summary>
public sealed partial class Sh2
{
    private static int SignExtend8(int value) => ((sbyte)value);
    private static int SignExtend12(int value) => (value << 20) >> 20;

    /// <summary>Fetches and executes the instruction immediately following a delayed branch,
    /// then applies <paramref name="target"/> to PC — see this class's and <see cref="Sh2.Step"/>'s
    /// doc comments for the full design. Only a nested BRA/BSR in the delay slot is checked as
    /// illegal, matching PicoDrive's own scope exactly (cpu/sh2/mame/sh2pico.c:128-136, itself
    /// marked "TODO: more branch types" there) — not the full illegal-slot-instruction opcode set
    /// the real SH-2 ISA defines. See this project's known-gaps remarks.</summary>
    private int ExecuteDelayedBranch(uint target, int branchCycles)
    {
        ushort slotOpcode = FetchWord();

        int slotTopNibble = slotOpcode >> 12;
        if (slotTopNibble is 0b1010 or 0b1011) // nested BRA / BSR
        {
            return RaiseIllegalSlotInstruction();
        }

        bool wasInDelaySlot = _inDelaySlot;
        _inDelaySlot = true;
        int slotCycles = Execute(slotOpcode);
        _inDelaySlot = wasInDelaySlot;

        PC = target;
        return branchCycles + slotCycles;
    }

    /// <summary>BRA disp12 — unconditional, delayed. cpu/sh2/mame/sh2.c:368-386 (the
    /// BUSY_LOOP_HACKS block there is a PicoDrive performance shortcut, not hardware behavior,
    /// and is not reproduced here). Own cost 2 (base 1 + 1).</summary>
    private int ExecuteBra(int disp12)
    {
        uint target = PC + (uint)(SignExtend12(disp12) * 2) + 2;
        return ExecuteDelayedBranch(target, branchCycles: 2);
    }

    /// <summary>BRAF Rm — unconditional, delayed, register-relative. cpu/sh2/mame/sh2.c:392-397.
    /// Own cost 2.</summary>
    private int ExecuteBraf(int m)
    {
        uint target = PC + R[m] + 2;
        return ExecuteDelayedBranch(target, branchCycles: 2);
    }

    /// <summary>BSR disp12 — delayed subroutine call. PR is set from PC *before* the delay slot
    /// runs (matching PicoDrive's own instruction ordering, cpu/sh2/mame/sh2.c:403-411) — real
    /// hardware doesn't defer PR the way it defers PC, only the jump itself is delayed. Own cost
    /// 2.</summary>
    private int ExecuteBsr(int disp12)
    {
        PR = PC + 2;
        uint target = PC + (uint)(SignExtend12(disp12) * 2) + 2;
        return ExecuteDelayedBranch(target, branchCycles: 2);
    }

    /// <summary>BSRF Rm — delayed subroutine call, register-relative. cpu/sh2/mame/sh2.c:417-423.
    /// Own cost 2.</summary>
    private int ExecuteBsrf(int m)
    {
        PR = PC + 2;
        uint target = PC + R[m] + 2;
        return ExecuteDelayedBranch(target, branchCycles: 2);
    }

    /// <summary>JMP @Rm — delayed, absolute. cpu/sh2/mame/sh2.c:864-869. Own cost 2.</summary>
    private int ExecuteJmp(int m) => ExecuteDelayedBranch(R[m], branchCycles: 2);

    /// <summary>JSR @Rm — delayed subroutine call, absolute. cpu/sh2/mame/sh2.c:872-878. Own
    /// cost 2.</summary>
    private int ExecuteJsr(int m)
    {
        PR = PC + 2;
        return ExecuteDelayedBranch(R[m], branchCycles: 2);
    }

    /// <summary>RTS — delayed return. cpu/sh2/mame/sh2.c:1497-1502. Own cost 2.</summary>
    private int ExecuteRts() => ExecuteDelayedBranch(PR, branchCycles: 2);

    /// <summary>RTE — delayed return-from-exception. Pops PC then SR (confirmed pop order
    /// against the push order used by ServicePendingInterrupt/RaiseIllegalInstruction/
    /// ExecuteTrapa — SR pushed first so it sits *above* PC on the stack, meaning PC pops
    /// first); SR is restored *immediately* (not deferred through the delay slot the way PC is —
    /// confirmed against PicoDrive's own instruction ordering, cpu/sh2/mame/sh2.c:1483-1494,
    /// where sr is assigned inside RTE's own body, before the delay-slot instruction ever runs),
    /// masked to just the real flag bits (T/S/I3-I0/Q/M). Own cost 4.</summary>
    private int ExecuteRte()
    {
        uint poppedPC = PopLong();
        SR = PopLong() & (FlagTBit | FlagSBit | InterruptMaskField | FlagQBit | FlagMBit);
        return ExecuteDelayedBranch(poppedPC, branchCycles: 4);
    }

    /// <summary>BT disp8 — conditional, *not* delayed: the target takes effect immediately, with
    /// no instruction after it unconditionally executing. cpu/sh2/mame/sh2.c:429-437. Cost 3 if
    /// taken, 1 if not (PicoDrive only charges the extra 2 inside the taken branch).</summary>
    private int ExecuteBt(int disp8)
    {
        if (!FlagT) return 1;
        PC = PC + (uint)(SignExtend8(disp8) * 2) + 2;
        return 3;
    }

    /// <summary>BF disp8 — conditional, not delayed. cpu/sh2/mame/sh2.c:337-345. Cost 3 if
    /// taken, 1 if not.</summary>
    private int ExecuteBf(int disp8)
    {
        if (FlagT) return 1;
        PC = PC + (uint)(SignExtend8(disp8) * 2) + 2;
        return 3;
    }

    /// <summary>BT.S disp8 — conditional *and* delayed: unlike BT, the following instruction
    /// always executes, taken or not (see this class's own doc comment). Confirmed against
    /// PicoDrive's BTS (cpu/sh2/mame/sh2.c:443-454): when not taken, the "target" is simply the
    /// address right after the delay slot, i.e. plain fallthrough.
    ///
    /// Own cost 2 if taken, 1 if not. A real, previously-shipped bug here charged 3/2 instead
    /// (one cycle too many either way) — PicoDrive's own BTS/BFS only do <c>sh2->icount--</c>
    /// inside the *taken* path (<c>mame/sh2.c:443-454</c>/<c>351-362</c>); the base 1 every
    /// instruction gets comes from its own interpreter loop's unconditional <c>icount--</c>
    /// (<c>sh2pico.c:169</c>), separate from any given instruction's own handler. This core has
    /// no equivalent shared "every instruction costs at least 1" decrement anywhere else — each
    /// <c>ExecuteXxx</c> method's return value *is* that instruction's total cost — so
    /// PicoDrive's "base(1) [+ extra(1) if taken]" collapses to a flat 1/2 here, not 2/3.
    /// Found via a real 32X title (Pitfall: The Mayan Adventure) whose loading-screen tile-blit
    /// loop is dominated by BF/S — the ~1-extra-cycle-per-iteration overcharge on the single most
    /// common instruction in that loop was enough to noticeably slow a render real hardware
    /// finishes in one frame, spilling it across several (visible as the render looking
    /// "still forming"/inconsistent frame-to-frame while under investigation).</summary>
    private int ExecuteBts(int disp8)
    {
        bool taken = FlagT;
        uint target = taken ? PC + (uint)(SignExtend8(disp8) * 2) + 2 : PC + 2;
        return ExecuteDelayedBranch(target, branchCycles: taken ? 2 : 1);
    }

    /// <summary>BF.S disp8 — conditional and delayed, mirroring BT.S. cpu/sh2/mame/
    /// sh2.c:351-362. Own cost 2 if taken, 1 if not — see BT.S's own remarks for why (the same
    /// fix applies here).</summary>
    private int ExecuteBfs(int disp8)
    {
        bool taken = !FlagT;
        uint target = taken ? PC + (uint)(SignExtend8(disp8) * 2) + 2 : PC + 2;
        return ExecuteDelayedBranch(target, branchCycles: taken ? 2 : 1);
    }
}
