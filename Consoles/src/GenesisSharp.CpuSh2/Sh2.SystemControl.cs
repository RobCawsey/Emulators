namespace GenesisSharp.CpuSh2;

public sealed partial class Sh2
{
    /// <summary>Confirmed against PicoDrive's ILLEGAL (cpu/sh2/mame/sh2.c:847-860): pushes SR
    /// then PC (PC = the illegal opcode's *own* address, i.e. PC-2 at the point this is called,
    /// since Step() already advanced PC past it during fetch — unlike a normal interrupt, which
    /// pushes the address of the *next* instruction), fetches the handler from vector 4, and
    /// costs 6 cycles total (PicoDrive's own comment: "TODO: timing is a guess").</summary>
    private int RaiseIllegalInstruction()
    {
        PushLong(SR);
        PushLong(PC - 2);
        PC = _bus.ReadLong(VBR + 4u * 4);
        return 6;
    }

    /// <summary>Confirmed against PicoDrive's illegal-slot-instruction handling
    /// (cpu/sh2/mame/sh2pico.c:128-136) — the one illegal-instruction variant PicoDrive itself
    /// only partially models (its own comment: "TODO: more branch types"; only a nested BRA/BSR
    /// in a delay slot is detected there). Vector 6, unlike plain ILLEGAL's vector 4. Pushes SR
    /// then the delay slot instruction's own address (mirroring RaiseIllegalInstruction's PC-2
    /// convention, since the slot instruction was likewise already fetched-past by the time this
    /// is called).</summary>
    private int RaiseIllegalSlotInstruction()
    {
        PushLong(SR);
        PushLong(PC - 2);
        PC = _bus.ReadLong(VBR + 6u * 4);
        return 6; // matches RaiseIllegalInstruction's cost; PicoDrive charges the same 5-extra + base
    }

    /// <summary>Confirmed against PicoDrive's NOP (cpu/sh2/mame/sh2.c:1412-1414) — a true no-op,
    /// costing only the baseline 1 cycle every instruction gets.</summary>
    private static int ExecuteNop() => 1;

    /// <summary>Confirmed against PicoDrive's CLRT (cpu/sh2/mame/sh2.c:470-473).</summary>
    private int ExecuteClrt()
    {
        FlagT = false;
        return 1;
    }

    /// <summary>Confirmed against PicoDrive's SETT (cpu/sh2/mame/sh2.c:1505-1508).</summary>
    private int ExecuteSett()
    {
        FlagT = true;
        return 1;
    }

    /// <summary>CLRMAC — zeroes MACH/MACL. cpu/sh2/mame/sh2.c:460-464.</summary>
    private int ExecuteClrmac()
    {
        MACH = 0;
        MACL = 0;
        return 1;
    }

    /// <summary>SLEEP — rewinds PC back onto itself so the same opcode re-executes every Step()
    /// until an interrupt is serviced (real hardware halts the bus and waits for one); cost 3
    /// (base 1 + PicoDrive's own "icount -= 2"). cpu/sh2/mame/sh2.c:1574-1585.</summary>
    private int ExecuteSleep()
    {
        PC -= 2;
        return 3;
    }

    // ---- STC (register form) / STS (register form) — dispatched from Group0 (top nibble 0000),
    // using only the "n" register field despite the mnemonic naming the source register "Rn" ----

    /// <summary>STC SR,Rn. cpu/sh2/mame/sh2.c:1587-1591.</summary>
    private int ExecuteStcSr(int n) { R[n] = SR; return 1; }

    /// <summary>STC GBR,Rn. cpu/sh2/mame/sh2.c:1593-1597.</summary>
    private int ExecuteStcGbr(int n) { R[n] = GBR; return 1; }

    /// <summary>STC VBR,Rn. cpu/sh2/mame/sh2.c:1599-1603.</summary>
    private int ExecuteStcVbr(int n) { R[n] = VBR; return 1; }

    /// <summary>STS MACH,Rn. cpu/sh2/mame/sh2.c:1632-1636.</summary>
    private int ExecuteStsMach(int n) { R[n] = MACH; return 1; }

    /// <summary>STS MACL,Rn. cpu/sh2/mame/sh2.c:1638-1642.</summary>
    private int ExecuteStsMacl(int n) { R[n] = MACL; return 1; }

    /// <summary>STS PR,Rn. cpu/sh2/mame/sh2.c:1644-1648.</summary>
    private int ExecuteStsPr(int n) { R[n] = PR; return 1; }

    // ---- LDC (register + memory-indirect forms) / LDS (register + memory-indirect forms) /
    // STC.L / STS.L (memory-indirect forms) — all dispatched from Group4 (top nibble 0100) ----

    /// <summary>LDC Rm,SR — only the T/S/I3-I0/Q/M bits are loaded, matching PicoDrive's own
    /// FLAGS mask (mirrors Sh2.ControlFlow.cs's RTE, which masks identically). PicoDrive
    /// additionally sets a `test_irq` scheduling hint here, which this core doesn't need:
    /// ServicePendingInterrupt() already re-evaluates against the current SR at the top of every
    /// Step(), so a changed interrupt mask takes effect on the very next Step() with no extra
    /// flag required. cpu/sh2/mame/sh2.c:881-886.</summary>
    private int ExecuteLdcSr(int m)
    {
        SR = R[m] & (FlagTBit | FlagSBit | InterruptMaskField | FlagQBit | FlagMBit);
        return 1;
    }

    /// <summary>LDC Rm,GBR. cpu/sh2/mame/sh2.c:888-892.</summary>
    private int ExecuteLdcGbr(int m) { GBR = R[m]; return 1; }

    /// <summary>LDC Rm,VBR. cpu/sh2/mame/sh2.c:894-898.</summary>
    private int ExecuteLdcVbr(int m) { VBR = R[m]; return 1; }

    /// <summary>LDC.L @Rm+,SR — post-increment load, same FLAGS masking as the register form.
    /// Costs 3 (base 1 + PicoDrive's own "icount -= 2"). cpu/sh2/mame/sh2.c:900-908.</summary>
    private int ExecuteLdcMSr(int m)
    {
        SR = _bus.ReadLong(R[m]) & (FlagTBit | FlagSBit | InterruptMaskField | FlagQBit | FlagMBit);
        R[m] += 4;
        return 3;
    }

    /// <summary>LDC.L @Rm+,GBR — costs 3. cpu/sh2/mame/sh2.c:910-917.</summary>
    private int ExecuteLdcMGbr(int m)
    {
        GBR = _bus.ReadLong(R[m]);
        R[m] += 4;
        return 3;
    }

    /// <summary>LDC.L @Rm+,VBR — costs 3. cpu/sh2/mame/sh2.c:919-926.</summary>
    private int ExecuteLdcMVbr(int m)
    {
        VBR = _bus.ReadLong(R[m]);
        R[m] += 4;
        return 3;
    }

    /// <summary>LDS Rm,MACH. cpu/sh2/mame/sh2.c:928-932.</summary>
    private int ExecuteLdsMach(int m) { MACH = R[m]; return 1; }

    /// <summary>LDS Rm,MACL. cpu/sh2/mame/sh2.c:934-938.</summary>
    private int ExecuteLdsMacl(int m) { MACL = R[m]; return 1; }

    /// <summary>LDS Rm,PR. cpu/sh2/mame/sh2.c:940-944.</summary>
    private int ExecuteLdsPr(int m) { PR = R[m]; return 1; }

    /// <summary>LDS.L @Rm+,MACH — post-increment load; unlike the LDC.L SR/GBR/VBR forms above,
    /// PicoDrive charges no extra cycles for this one, so it costs only the base 1.
    /// cpu/sh2/mame/sh2.c:946-952.</summary>
    private int ExecuteLdsMMach(int m)
    {
        MACH = _bus.ReadLong(R[m]);
        R[m] += 4;
        return 1;
    }

    /// <summary>LDS.L @Rm+,MACL — costs 1, same reasoning as <see cref="ExecuteLdsMMach"/>.
    /// cpu/sh2/mame/sh2.c:954-960.</summary>
    private int ExecuteLdsMMacl(int m)
    {
        MACL = _bus.ReadLong(R[m]);
        R[m] += 4;
        return 1;
    }

    /// <summary>LDS.L @Rm+,PR — costs 1, same reasoning. cpu/sh2/mame/sh2.c:962-968.</summary>
    private int ExecuteLdsMPr(int m)
    {
        PR = _bus.ReadLong(R[m]);
        R[m] += 4;
        return 1;
    }

    /// <summary>STC.L SR,@-Rn — pre-decrement store; costs 2 (base 1 + PicoDrive's own
    /// "icount--"). cpu/sh2/mame/sh2.c:1605-1612.</summary>
    private int ExecuteStcMSr(int n)
    {
        R[n] -= 4;
        _bus.WriteLong(R[n], SR);
        return 2;
    }

    /// <summary>STC.L GBR,@-Rn — costs 2. cpu/sh2/mame/sh2.c:1614-1621.</summary>
    private int ExecuteStcMGbr(int n)
    {
        R[n] -= 4;
        _bus.WriteLong(R[n], GBR);
        return 2;
    }

    /// <summary>STC.L VBR,@-Rn — costs 2. cpu/sh2/mame/sh2.c:1623-1630.</summary>
    private int ExecuteStcMVbr(int n)
    {
        R[n] -= 4;
        _bus.WriteLong(R[n], VBR);
        return 2;
    }

    /// <summary>STS.L MACH,@-Rn — pre-decrement store; unlike the STC.L SR/GBR/VBR forms above,
    /// PicoDrive charges no extra cycles for this one, so it costs only the base 1.
    /// cpu/sh2/mame/sh2.c:1650-1656.</summary>
    private int ExecuteStsMMach(int n)
    {
        R[n] -= 4;
        _bus.WriteLong(R[n], MACH);
        return 1;
    }

    /// <summary>STS.L MACL,@-Rn — costs 1, same reasoning as <see cref="ExecuteStsMMach"/>.
    /// cpu/sh2/mame/sh2.c:1658-1664.</summary>
    private int ExecuteStsMMacl(int n)
    {
        R[n] -= 4;
        _bus.WriteLong(R[n], MACL);
        return 1;
    }

    /// <summary>STS.L PR,@-Rn — costs 1, same reasoning. cpu/sh2/mame/sh2.c:1666-1672.</summary>
    private int ExecuteStsMPr(int n)
    {
        R[n] -= 4;
        _bus.WriteLong(R[n], PR);
        return 1;
    }

    /// <summary>TAS.B @Rn — test-and-set: reads a byte, sets T if it was zero, then
    /// unconditionally ORs in bit 7 and writes it back (real hardware does this as one locked
    /// bus cycle; this core has no bus contention to model, so the read/modify/write is just
    /// sequential). Costs 4 (base 1 + PicoDrive's own "icount -= 3"). cpu/sh2/mame/sh2.c:1747-1762.</summary>
    private int ExecuteTas(int n)
    {
        byte value = _bus.ReadByte(R[n]);
        FlagT = value == 0;
        _bus.WriteByte(R[n], (byte)(value | 0x80));
        return 4;
    }

    /// <summary>TRAPA #imm — software trap. Confirmed against PicoDrive's TRAPA
    /// (cpu/sh2/mame/sh2.c:1765-1779): pushes SR then PC (PC here is already the address of the
    /// *next* instruction, unlike the illegal-instruction exceptions above, since TRAPA is a
    /// deliberate call rather than a decode failure), fetches the handler from
    /// <c>VBR + imm*4</c>, costs 8 cycles total.</summary>
    private int ExecuteTrapa(int imm)
    {
        uint vectorAddress = VBR + (uint)(imm & 0xFF) * 4;
        PushLong(SR);
        PushLong(PC);
        PC = _bus.ReadLong(vectorAddress);
        return 8;
    }
}
