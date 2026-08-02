namespace GenesisSharp.CpuSh2;

/// <summary>MUL/DMUL/MAC family. Confirmed against PicoDrive's cpu/sh2/mame/sh2.c (citations per
/// method). PicoDrive computes the wide (32x32-&gt;64 and the MAC family's accumulation) results
/// via a manual schoolbook expansion into 16-bit partial products — a necessity for the C
/// standard PicoDrive targets, not a hardware fact. This file uses C#'s native 64-bit integer
/// arithmetic instead for the parts that are pure multiplication (DMULS/DMULU, and MAC.L/MAC.W's
/// own product term): mathematically the exact same operation, verified equivalent by hand
/// against PicoDrive's expansion for representative and boundary values, not an approximation.
/// The *accumulation and saturation* logic in MAC.L/MAC.W is genuine SH-2 semantics, not an
/// implementation detail of PicoDrive's target language, and is ported line-for-line rather than
/// reformulated, to avoid introducing a sign-handling mistake through "clever" restructuring of
/// already-subtle carry/overflow bookkeeping.</summary>
public sealed partial class Sh2
{
    /// <summary>MUL.L Rm,Rn — MACL = low 32 bits of Rn*Rm (unsigned wraparound, so signedness of
    /// the inputs doesn't matter for this truncated result). cpu/sh2/mame/sh2.c:1374-1378. Own
    /// cost 2 (base 1 + 1).</summary>
    private int ExecuteMull(int m, int n) { MACL = unchecked(R[n] * R[m]); return 2; }

    /// <summary>MULS.W Rm,Rn — 16x16-&gt;32 signed multiply, only the low 16 bits of each operand
    /// participate. cpu/sh2/mame/sh2.c:1381-1384.</summary>
    private int ExecuteMuls(int m, int n)
    {
        MACL = unchecked((uint)((short)R[n] * (short)R[m]));
        return 1;
    }

    /// <summary>MULU.W Rm,Rn — 16x16-&gt;32 unsigned multiply. cpu/sh2/mame/sh2.c:1387-1390.</summary>
    private int ExecuteMulu(int m, int n)
    {
        MACL = (ushort)R[n] * (uint)(ushort)R[m];
        return 1;
    }

    /// <summary>DMULS.L Rm,Rn — full signed 32x32-&gt;64 multiply, MACH:MACL = the product.
    /// cpu/sh2/mame/sh2.c:719-765 (PicoDrive's own schoolbook expansion of exactly this
    /// operation). Own cost 2.</summary>
    private int ExecuteDmuls(int m, int n)
    {
        long product = (long)(int)R[n] * (int)R[m];
        MACH = unchecked((uint)((ulong)product >> 32));
        MACL = unchecked((uint)product);
        return 2;
    }

    /// <summary>DMULU.L Rm,Rn — full unsigned 32x32-&gt;64 multiply. cpu/sh2/mame/sh2.c:768-815ish
    /// (PicoDrive's own schoolbook expansion of exactly this operation). Own cost 2.</summary>
    private int ExecuteDmulu(int m, int n)
    {
        ulong product = (ulong)R[n] * R[m];
        MACH = (uint)(product >> 32);
        MACL = (uint)product;
        return 2;
    }

    /// <summary>MAC.L @Rm+,@Rn+ — reads two longwords (post-incrementing both pointers by 4),
    /// accumulates their signed 64-bit product into MACH:MACL. Confirmed against PicoDrive's
    /// MAC_L (cpu/sh2/mame/sh2.c:971-1045): with the S (saturation) flag set, only MACH's low 16
    /// bits participate in the accumulator (an effectively 48-bit-wide saturating range,
    /// clamping at 0x0000800000000000/0x00007FFFFFFFFFFF); with S clear, the full 64 bits
    /// accumulate with silent wraparound. Own cost 3 (base 1 + PicoDrive's extra 2).</summary>
    private int ExecuteMacL(int m, int n)
    {
        int valueN = unchecked((int)_bus.ReadLong(R[n]));
        R[n] += 4;
        int valueM = unchecked((int)_bus.ReadLong(R[m]));
        R[m] += 4;

        long product = (long)valueN * valueM;
        uint productLow = unchecked((uint)product);
        uint productHigh = unchecked((uint)((ulong)product >> 32));

        uint newLow = unchecked(MACL + productLow);
        uint carry = newLow < MACL ? 1u : 0u;

        if (FlagS)
        {
            uint newHigh = unchecked(productHigh + (MACH & 0xFFFF) + carry);
            if ((int)newHigh < 0 && newHigh < 0xFFFF_8000)
            {
                newHigh = 0x0000_8000;
                newLow = 0x0000_0000;
            }
            else if ((int)newHigh > 0 && newHigh > 0x0000_7FFF)
            {
                newHigh = 0x0000_7FFF;
                newLow = 0xFFFF_FFFF;
            }
            MACH = newHigh;
            MACL = newLow;
        }
        else
        {
            MACH = unchecked(productHigh + MACH + carry);
            MACL = newLow;
        }

        return 3;
    }

    /// <summary>MAC.W @Rm+,@Rn+ — reads two words (post-incrementing both pointers by 2),
    /// accumulates their signed 16x16-&gt;32 product. Confirmed against PicoDrive's MAC_W
    /// (cpu/sh2/mame/sh2.c:1048-1097): with S set, MACL alone saturates as a 32-bit signed
    /// accumulator (MACH untouched); with S clear, the product is sign-extended and accumulated
    /// across the full MACH:MACL 64 bits. Own cost 3.</summary>
    private int ExecuteMacW(int m, int n)
    {
        int valueN = unchecked((int)(short)_bus.ReadWord(R[n]));
        R[n] += 2;
        int valueM = unchecked((int)(short)_bus.ReadWord(R[m]));
        R[m] += 2;
        int product = valueN * valueM; // 16x16 -> fits safely in a signed 32-bit int

        uint oldMacl = MACL;
        int dest = (int)oldMacl >= 0 ? 0 : 1;
        int src = product >= 0 ? 0 : 1;
        int signExtendedProductHigh = product >= 0 ? 0 : -1;
        src += dest;

        MACL = unchecked(oldMacl + (uint)product);

        int ans = ((int)MACL >= 0 ? 0 : 1) + dest;

        if (FlagS)
        {
            if (ans == 1)
            {
                if (src == 0) MACL = 0x7FFF_FFFF;
                if (src == 2) MACL = 0x8000_0000;
            }
        }
        else
        {
            MACH = unchecked(MACH + (uint)signExtendedProductHigh);
            if (oldMacl > MACL) MACH++;
        }

        return 3;
    }
}
