namespace GenesisSharp.CpuSh2;

/// <summary>Shifts and rotates. Confirmed line-for-line against PicoDrive's
/// cpu/sh2/mame/sh2.c (citations per method). The fixed-count forms (SHLL2/8/16, SHLR2/8/16)
/// don't touch T at all — confirmed by their bodies containing no SR reference whatsoever,
/// unlike the single-bit forms, which is easy to assume is a transcription oversight rather
/// than the real, intentional distinction it is.</summary>
public sealed partial class Sh2
{
    /// <summary>SHAL Rn — arithmetic shift left; identical bit operation to SHLL, kept as a
    /// separate opcode/mnemonic on real hardware. cpu/sh2/mame/sh2.c:1511-1515.</summary>
    private int ExecuteShal(int n) { FlagT = (R[n] & 0x8000_0000) != 0; R[n] <<= 1; return 1; }

    /// <summary>SHAR Rn — arithmetic shift right (sign-preserving). cpu/sh2/mame/sh2.c:1518-1522.</summary>
    private int ExecuteShar(int n) { FlagT = (R[n] & 1) != 0; R[n] = unchecked((uint)((int)R[n] >> 1)); return 1; }

    /// <summary>SHLL Rn — logical shift left by 1, T = bit shifted out. cpu/sh2/mame/sh2.c:1525-1529.</summary>
    private int ExecuteShll(int n) { FlagT = (R[n] & 0x8000_0000) != 0; R[n] <<= 1; return 1; }

    /// <summary>SHLR Rn — logical shift right by 1, T = bit shifted out. cpu/sh2/mame/sh2.c:1550-1554.</summary>
    private int ExecuteShlr(int n) { FlagT = (R[n] & 1) != 0; R[n] >>= 1; return 1; }

    /// <summary>SHLL2/8/16 Rn — fixed-count logical shift left; T is left alone.
    /// cpu/sh2/mame/sh2.c:1532-1547.</summary>
    private int ExecuteShll2(int n) { R[n] <<= 2; return 1; }
    private int ExecuteShll8(int n) { R[n] <<= 8; return 1; }
    private int ExecuteShll16(int n) { R[n] <<= 16; return 1; }

    /// <summary>SHLR2/8/16 Rn — fixed-count logical shift right; T is left alone.
    /// cpu/sh2/mame/sh2.c:1556-1572.</summary>
    private int ExecuteShlr2(int n) { R[n] >>= 2; return 1; }
    private int ExecuteShlr8(int n) { R[n] >>= 8; return 1; }
    private int ExecuteShlr16(int n) { R[n] >>= 16; return 1; }

    /// <summary>ROTL Rn — rotate left by 1; T = bit rotated out (= bit rotated back in at
    /// position 0). cpu/sh2/mame/sh2.c:1469-1473.</summary>
    private int ExecuteRotl(int n) { FlagT = (R[n] & 0x8000_0000) != 0; R[n] = (R[n] << 1) | (R[n] >> 31); return 1; }

    /// <summary>ROTR Rn — rotate right by 1. cpu/sh2/mame/sh2.c:1476-1480.</summary>
    private int ExecuteRotr(int n) { FlagT = (R[n] & 1) != 0; R[n] = (R[n] >> 1) | (R[n] << 31); return 1; }

    /// <summary>ROTCL Rn — rotate left through carry: T shifts in at bit 0, the bit shifted out
    /// becomes the new T. Note T is read *before* being overwritten, since the old T is what
    /// feeds back into bit 0. cpu/sh2/mame/sh2.c:1447-1454.</summary>
    private int ExecuteRotcl(int n)
    {
        bool carryOut = (R[n] & 0x8000_0000) != 0;
        R[n] = (R[n] << 1) | (FlagT ? 1u : 0u);
        FlagT = carryOut;
        return 1;
    }

    /// <summary>ROTCR Rn — rotate right through carry, mirroring ROTCL. cpu/sh2/mame/
    /// sh2.c:1457-1466.</summary>
    private int ExecuteRotcr(int n)
    {
        bool carryOut = (R[n] & 1) != 0;
        R[n] = (R[n] >> 1) | (FlagT ? 0x8000_0000u : 0u);
        FlagT = carryOut;
        return 1;
    }
}
