namespace GenesisSharp.CpuSh2;

/// <summary>ADD/SUB families and CMP/DT. Confirmed line-for-line against PicoDrive's
/// cpu/sh2/mame/sh2.c (citations per method). CMP/STR is deliberately deferred — see this
/// project's known-gaps remarks — rather than transcribed without having read it as carefully as
/// everything here.</summary>
public sealed partial class Sh2
{
    /// <summary>ADD Rm,Rn — no flags affected. cpu/sh2/mame/sh2.c:233-236.</summary>
    private int ExecuteAdd(int m, int n) { R[n] += R[m]; return 1; }

    /// <summary>ADD #imm,Rn — 8-bit immediate, sign-extended; no flags affected.
    /// cpu/sh2/mame/sh2.c:242-245.</summary>
    private int ExecuteAddImmediate(int imm8, int n) { R[n] += SignExtendByte((byte)imm8); return 1; }

    /// <summary>ADDC Rm,Rn — adds with carry-in from T, sets T to the carry-out. The source runs
    /// *two* separate overflow checks (the plain add, then whether adding the carry-in bit itself
    /// overflows) — both are needed and are ported literally rather than collapsed into one
    /// comparison, after an initial single-check version of this method shipped without a test
    /// and was later found to give the wrong T for e.g. R[n]=0xFFFFFFFF,R[m]=0,T=1 (should carry
    /// out, didn't). cpu/sh2/mame/sh2.c:251-261.</summary>
    private int ExecuteAddc(int m, int n)
    {
        uint tmp1 = unchecked(R[n] + R[m]);
        uint tmp0 = R[n];
        R[n] = unchecked(tmp1 + (SR & 1));
        FlagT = tmp0 > tmp1;
        if (tmp1 > R[n]) FlagT = true; // adding the carry-in itself overflowed; sticky-set only
        return 1;
    }

    /// <summary>SUB Rm,Rn — no flags affected. cpu/sh2/mame/sh2.c:1674-1678.</summary>
    private int ExecuteSub(int m, int n) { R[n] = unchecked(R[n] - R[m]); return 1; }

    /// <summary>SUBC Rm,Rn — subtracts with borrow-in from T, sets T to the borrow-out. Same
    /// two-check shape as <see cref="ExecuteAddc"/>, ported literally for the same reason.
    /// cpu/sh2/mame/sh2.c:1680-1694.</summary>
    private int ExecuteSubc(int m, int n)
    {
        uint tmp1 = unchecked(R[n] - R[m]);
        uint tmp0 = R[n];
        R[n] = unchecked(tmp1 - (SR & 1));
        FlagT = tmp0 < tmp1;
        if (tmp1 < R[n]) FlagT = true; // subtracting the borrow-in itself underflowed
        return 1;
    }

    /// <summary>ADDV Rm,Rn — signed add, T set on signed overflow. Ported literally from the
    /// source's dest/src/ans sign-bookkeeping rather than reformulated. cpu/sh2/mame/sh2.c:270-298.</summary>
    private int ExecuteAddv(int m, int n)
    {
        int dest = (int)R[n] >= 0 ? 0 : 1;
        int src = (int)R[m] >= 0 ? 0 : 1;
        src += dest;
        R[n] = unchecked(R[n] + R[m]);
        int ans = ((int)R[n] >= 0 ? 0 : 1) + dest;
        FlagT = (src == 0 || src == 2) && ans == 1;
        return 1;
    }

    /// <summary>SUBV Rm,Rn — signed subtract, T set on signed overflow. Same shape as
    /// <see cref="ExecuteAddv"/>. cpu/sh2/mame/sh2.c:1696-1725.</summary>
    private int ExecuteSubv(int m, int n)
    {
        int dest = (int)R[n] >= 0 ? 0 : 1;
        int src = (int)R[m] >= 0 ? 0 : 1;
        src += dest;
        R[n] = unchecked(R[n] - R[m]);
        int ans = ((int)R[n] >= 0 ? 0 : 1) + dest;
        FlagT = src == 1 && ans == 1;
        return 1;
    }

    /// <summary>NEG Rm,Rn — two's-complement negate, no flags affected. cpu/sh2/mame/sh2.c:1392-1396.</summary>
    private int ExecuteNeg(int m, int n) { R[n] = unchecked(0u - R[m]); return 1; }

    /// <summary>NEGC Rm,Rn — negate with borrow-in from T; T is set to the borrow-out. Simpler
    /// single-condition shape than ADDC/SUBC's two-check pattern (still ported literally, not
    /// re-derived), since the source only needs one comparison here.
    /// cpu/sh2/mame/sh2.c:1398-1409.</summary>
    private int ExecuteNegc(int m, int n)
    {
        uint temp = R[m];
        bool carryIn = FlagT;
        R[n] = unchecked(0u - temp - (carryIn ? 1u : 0u));
        FlagT = temp != 0 || carryIn;
        return 1;
    }

    /// <summary>CMP/EQ Rm,Rn. cpu/sh2/mame/sh2.c:479-485.</summary>
    private int ExecuteCmpEq(int m, int n) { FlagT = R[n] == R[m]; return 1; }

    /// <summary>CMP/GE Rm,Rn — signed. cpu/sh2/mame/sh2.c:491-497.</summary>
    private int ExecuteCmpGe(int m, int n) { FlagT = (int)R[n] >= (int)R[m]; return 1; }

    /// <summary>CMP/GT Rm,Rn — signed. cpu/sh2/mame/sh2.c:503-509.</summary>
    private int ExecuteCmpGt(int m, int n) { FlagT = (int)R[n] > (int)R[m]; return 1; }

    /// <summary>CMP/HI Rm,Rn — unsigned. cpu/sh2/mame/sh2.c:515-521.</summary>
    private int ExecuteCmpHi(int m, int n) { FlagT = R[n] > R[m]; return 1; }

    /// <summary>CMP/HS Rm,Rn — unsigned. cpu/sh2/mame/sh2.c:527-533.</summary>
    private int ExecuteCmpHs(int m, int n) { FlagT = R[n] >= R[m]; return 1; }

    /// <summary>CMP/PL Rn — signed, against zero. cpu/sh2/mame/sh2.c:540-546.</summary>
    private int ExecuteCmpPl(int n) { FlagT = (int)R[n] > 0; return 1; }

    /// <summary>CMP/PZ Rn — signed, against zero. cpu/sh2/mame/sh2.c:552-558.</summary>
    private int ExecuteCmpPz(int n) { FlagT = (int)R[n] >= 0; return 1; }

    /// <summary>CMP/EQ #imm,R0 — 8-bit immediate, sign-extended. cpu/sh2/mame/sh2.c:584-592.</summary>
    private int ExecuteCmpImmediate(int imm8)
    {
        FlagT = R[0] == SignExtendByte((byte)imm8);
        return 1;
    }

    /// <summary>DT Rn — decrement and test for zero; the standard SH-2 loop-counter idiom
    /// (paired with BF/BT). cpu/sh2/mame/sh2.c:796-803 (the BUSY_LOOP_HACKS block there is a
    /// PicoDrive performance shortcut, not hardware behavior, and is not reproduced here).</summary>
    private int ExecuteDt(int n)
    {
        R[n]--;
        FlagT = R[n] == 0;
        return 1;
    }
}
