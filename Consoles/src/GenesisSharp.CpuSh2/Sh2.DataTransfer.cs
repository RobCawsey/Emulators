namespace GenesisSharp.CpuSh2;

/// <summary>MOV family, MOVA, and MOVT. Every instruction here is confirmed line-for-line
/// against PicoDrive's cpu/sh2/mame/sh2.c (citations per method) — bit-field positions (which
/// nibble is Rn vs Rm vs a displacement) come from the dispatch call sites in
/// Sh2.Decode.cs, cross-checked against each instruction's own comment header in that
/// file (e.g. "0010 nnnn mmmm 0000" for MOV.B Rm,@Rn).</summary>
public sealed partial class Sh2
{
    private static uint SignExtendByte(byte value) => unchecked((uint)(int)(sbyte)value);
    private static uint SignExtendWord(ushort value) => unchecked((uint)(int)(short)value);

    /// <summary>MOV Rm,Rn. cpu/sh2/mame/sh2.c:1100-1103.</summary>
    private int ExecuteMov(int m, int n)
    {
        R[n] = R[m];
        return 1;
    }

    /// <summary>MOV.B/W/L Rm,@Rn. cpu/sh2/mame/sh2.c:1106-1124.</summary>
    private int ExecuteMovBS(int m, int n) { _bus.WriteByte(R[n], (byte)R[m]); return 1; }
    private int ExecuteMovWS(int m, int n) { _bus.WriteWord(R[n], (ushort)R[m]); return 1; }
    private int ExecuteMovLS(int m, int n) { _bus.WriteLong(R[n], R[m]); return 1; }

    /// <summary>MOV.B/W/L @Rm,Rn — sign-extended for B/W. cpu/sh2/mame/sh2.c:1127-1145.</summary>
    private int ExecuteMovBL(int m, int n) { R[n] = SignExtendByte(_bus.ReadByte(R[m])); return 1; }
    private int ExecuteMovWL(int m, int n) { R[n] = SignExtendWord(_bus.ReadWord(R[m])); return 1; }
    private int ExecuteMovLL(int m, int n) { R[n] = _bus.ReadLong(R[m]); return 1; }

    /// <summary>MOV.B/W/L Rm,@-Rn — predecrement store. Source register is read before Rn is
    /// decremented, matching PicoDrive's own "bug fix, was reading sh2->r[n]" comment: Rm and Rn
    /// can be the same register, so capturing the value first (not re-reading R[n] after the
    /// decrement) matters. cpu/sh2/mame/sh2.c:1148-1173.</summary>
    private int ExecuteMovBM(int m, int n) { byte data = (byte)R[m]; R[n] -= 1; _bus.WriteByte(R[n], data); return 1; }
    private int ExecuteMovWM(int m, int n) { ushort data = (ushort)R[m]; R[n] -= 2; _bus.WriteWord(R[n], data); return 1; }
    private int ExecuteMovLM(int m, int n) { uint data = R[m]; R[n] -= 4; _bus.WriteLong(R[n], data); return 1; }

    /// <summary>MOV.B/W/L @Rm+,Rn — postincrement load. Rm is left alone when Rm==Rn (matches
    /// PicoDrive's explicit `if (n != m)` guard) — the loaded value would otherwise be
    /// immediately clobbered by the pointer update. cpu/sh2/mame/sh2.c:1176-1197.</summary>
    private int ExecuteMovBP(int m, int n) { R[n] = SignExtendByte(_bus.ReadByte(R[m])); if (n != m) R[m] += 1; return 1; }
    private int ExecuteMovWP(int m, int n) { R[n] = SignExtendWord(_bus.ReadWord(R[m])); if (n != m) R[m] += 2; return 1; }
    private int ExecuteMovLP(int m, int n) { R[n] = _bus.ReadLong(R[m]); if (n != m) R[m] += 4; return 1; }

    /// <summary>MOV.B/W/L Rm,@(R0,Rn) — R0-indexed store. cpu/sh2/mame/sh2.c:1200-1218.</summary>
    private int ExecuteMovBS0(int m, int n) { _bus.WriteByte(R[n] + R[0], (byte)R[m]); return 1; }
    private int ExecuteMovWS0(int m, int n) { _bus.WriteWord(R[n] + R[0], (ushort)R[m]); return 1; }
    private int ExecuteMovLS0(int m, int n) { _bus.WriteLong(R[n] + R[0], R[m]); return 1; }

    /// <summary>MOV.B/W/L @(R0,Rm),Rn — R0-indexed load. cpu/sh2/mame/sh2.c:1221-1239.</summary>
    private int ExecuteMovBL0(int m, int n) { R[n] = SignExtendByte(_bus.ReadByte(R[m] + R[0])); return 1; }
    private int ExecuteMovWL0(int m, int n) { R[n] = SignExtendWord(_bus.ReadWord(R[m] + R[0])); return 1; }
    private int ExecuteMovLL0(int m, int n) { R[n] = _bus.ReadLong(R[m] + R[0]); return 1; }

    /// <summary>MOV #imm,Rn — 8-bit immediate, sign-extended to 32 bits.
    /// cpu/sh2/mame/sh2.c:1242-1245.</summary>
    private int ExecuteMovImmediate(int imm8, int n) { R[n] = SignExtendByte((byte)imm8); return 1; }

    /// <summary>MOV.W @(disp8,PC),Rn. The displacement is unsigned, scaled by 2, and — critically
    /// — relative to PC as it stands *after* this instruction's own fetch (i.e. the value this
    /// core's PC already holds at execution time, per Step()'s fetch-then-execute order) plus a
    /// further +2, matching real SH-2's documented "address of this instruction + 4" PC-relative
    /// convention. cpu/sh2/mame/sh2.c:1248-1253.</summary>
    private int ExecuteMovWI(int disp8, int n) { R[n] = SignExtendWord(_bus.ReadWord(PC + (uint)disp8 * 2 + 2)); return 1; }

    /// <summary>MOV.L @(disp8,PC),Rn — same PC-relative convention as MOVWI, but the base address
    /// is additionally longword-aligned (low 2 bits of PC+2 masked off) before the displacement
    /// is applied, matching real SH-2 behavior for this specific form.
    /// cpu/sh2/mame/sh2.c:1256-1261.</summary>
    private int ExecuteMovLI(int disp8, int n) { R[n] = _bus.ReadLong(((PC + 2) & ~3u) + (uint)disp8 * 4); return 1; }

    /// <summary>MOV.B/W/L @(disp8,GBR),R0. cpu/sh2/mame/sh2.c:1264-1285.</summary>
    private int ExecuteMovBLG(int disp8) { R[0] = SignExtendByte(_bus.ReadByte(GBR + (uint)disp8)); return 1; }
    private int ExecuteMovWLG(int disp8) { R[0] = SignExtendWord(_bus.ReadWord(GBR + (uint)disp8 * 2)); return 1; }
    private int ExecuteMovLLG(int disp8) { R[0] = _bus.ReadLong(GBR + (uint)disp8 * 4); return 1; }

    /// <summary>MOV.B/W/L R0,@(disp8,GBR). cpu/sh2/mame/sh2.c:1288-1309.</summary>
    private int ExecuteMovBSG(int disp8) { _bus.WriteByte(GBR + (uint)disp8, (byte)R[0]); return 1; }
    private int ExecuteMovWSG(int disp8) { _bus.WriteWord(GBR + (uint)disp8 * 2, (ushort)R[0]); return 1; }
    private int ExecuteMovLSG(int disp8) { _bus.WriteLong(GBR + (uint)disp8 * 4, R[0]); return 1; }

    /// <summary>MOV.B/W R0,@(disp4,Rn) — small-displacement store, R0 as the fixed source.
    /// cpu/sh2/mame/sh2.c:1312-1325.</summary>
    private int ExecuteMovBS4(int disp4, int n) { _bus.WriteByte(R[n] + (uint)disp4, (byte)R[0]); return 1; }
    private int ExecuteMovWS4(int disp4, int n) { _bus.WriteWord(R[n] + (uint)disp4 * 2, (ushort)R[0]); return 1; }

    /// <summary>MOV.L Rm,@(disp4,Rn) — small-displacement store, explicit source register (unlike
    /// the B/W forms above, which are hardwired to R0). cpu/sh2/mame/sh2.c:1328-1333.</summary>
    private int ExecuteMovLS4(int m, int disp4, int n) { _bus.WriteLong(R[n] + (uint)disp4 * 4, R[m]); return 1; }

    /// <summary>MOV.B/W @(disp4,Rm),R0 — small-displacement load, R0 as the fixed destination.
    /// cpu/sh2/mame/sh2.c:1336-1349.</summary>
    private int ExecuteMovBL4(int m, int disp4) { R[0] = SignExtendByte(_bus.ReadByte(R[m] + (uint)disp4)); return 1; }
    private int ExecuteMovWL4(int m, int disp4) { R[0] = SignExtendWord(_bus.ReadWord(R[m] + (uint)disp4 * 2)); return 1; }

    /// <summary>MOV.L @(disp4,Rm),Rn — small-displacement load, explicit destination register.
    /// cpu/sh2/mame/sh2.c:1352-1357.</summary>
    private int ExecuteMovLL4(int m, int disp4, int n) { R[n] = _bus.ReadLong(R[m] + (uint)disp4 * 4); return 1; }

    /// <summary>MOVA @(disp8,PC),R0 — loads an *address*, not a value; same PC-relative
    /// longword-aligned base as MOVLI. cpu/sh2/mame/sh2.c:1360-1365.</summary>
    private int ExecuteMova(int disp8) { R[0] = ((PC + 2) & ~3u) + (uint)disp8 * 4; return 1; }

    /// <summary>MOVT Rn — Rn = T (0 or 1). cpu/sh2/mame/sh2.c:1368-1371.</summary>
    private int ExecuteMovt(int n) { R[n] = FlagT ? 1u : 0u; return 1; }

    /// <summary>XTRCT Rm,Rn — extracts the middle 32 bits of the 64-bit pair Rm:Rn (Rn's high
    /// half concatenated with Rm's low half). cpu/sh2/mame/sh2.c:1840-1848.</summary>
    private int ExecuteXtrct(int m, int n)
    {
        uint temp = (R[m] << 16) & 0xFFFF_0000u;
        R[n] = (R[n] >> 16) & 0x0000_FFFFu;
        R[n] |= temp;
        return 1;
    }

    /// <summary>SWAP.B Rm,Rn — swaps the low two bytes of Rm, high 16 bits pass through
    /// unchanged. cpu/sh2/mame/sh2.c:1727-1736.</summary>
    private int ExecuteSwapB(int m, int n)
    {
        uint high = R[m] & 0xFFFF_0000u;
        uint lowByteToHigh = (R[m] & 0x0000_00FFu) << 8;
        R[n] = ((R[m] >> 8) & 0x0000_00FFu) | lowByteToHigh | high;
        return 1;
    }

    /// <summary>SWAP.W Rm,Rn — swaps the two 16-bit halves of Rm. cpu/sh2/mame/sh2.c:1738-1745.</summary>
    private int ExecuteSwapW(int m, int n)
    {
        uint temp = (R[m] >> 16) & 0x0000_FFFFu;
        R[n] = (R[m] << 16) | temp;
        return 1;
    }

    /// <summary>EXTU.B Rm,Rn — zero-extends the low byte of Rm. cpu/sh2/mame/sh2.c:834-838.</summary>
    private int ExecuteExtuB(int m, int n) { R[n] = R[m] & 0x0000_00FFu; return 1; }

    /// <summary>EXTU.W Rm,Rn — zero-extends the low word of Rm. cpu/sh2/mame/sh2.c:840-844.</summary>
    private int ExecuteExtuW(int m, int n) { R[n] = R[m] & 0x0000_FFFFu; return 1; }

    /// <summary>EXTS.B Rm,Rn — sign-extends the low byte of Rm. cpu/sh2/mame/sh2.c:822-826.</summary>
    private int ExecuteExtsB(int m, int n) { R[n] = SignExtendByte((byte)R[m]); return 1; }

    /// <summary>EXTS.W Rm,Rn — sign-extends the low word of Rm. cpu/sh2/mame/sh2.c:828-832.</summary>
    private int ExecuteExtsW(int m, int n) { R[n] = SignExtendWord((ushort)R[m]); return 1; }
}
