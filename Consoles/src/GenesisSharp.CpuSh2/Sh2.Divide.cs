namespace GenesisSharp.CpuSh2;

/// <summary>DIV0S/DIV0U/DIV1 — SH-2 exposes division as a software loop of these single-cycle
/// primitives (one DIV1 per output bit), not a single multi-cycle divide instruction the way
/// 68000's DIVU/DIVS is. Unlike 68000's divide timing (an acknowledged average-case
/// approximation in this codebase's M68000 core, since real timing is genuinely data-dependent),
/// no such approximation is needed here: each of these instructions has a fixed, small cost
/// confirmed against PicoDrive. All three bodies are ported verbatim from PicoDrive's
/// cpu/sh2/mame/sh2.c rather than reformulated — DIV1's Q/M/T bit-twiddling in particular is
/// exactly the kind of subtle carry logic this codebase's own culture (see MAC.L/MAC.W) treats
/// as too easy to get wrong via a "cleverer" rewrite.</summary>
public sealed partial class Sh2
{
    /// <summary>DIV0S Rm,Rn — seeds Q from Rn's sign, M from Rm's sign, T from whether they
    /// differ (the initial "will this division be negative" setup, consumed by the DIV1 loop
    /// that follows). cpu/sh2/mame/sh2.c:598-612.</summary>
    private int ExecuteDiv0s(int m, int n)
    {
        FlagQ = (R[n] & 0x8000_0000) != 0;
        FlagM = (R[m] & 0x8000_0000) != 0;
        FlagT = FlagQ != FlagM;
        return 1;
    }

    /// <summary>DIV0U — clears Q, M, and T (the unsigned-division setup). cpu/sh2/mame/
    /// sh2.c:618-621.</summary>
    private int ExecuteDiv0u()
    {
        FlagQ = false;
        FlagM = false;
        FlagT = false;
        return 1;
    }

    /// <summary>DIV1 Rm,Rn — one step of the standard non-restoring division algorithm; a
    /// 32-bit division needs this executed once per output bit (typically wrapped in a
    /// ROTCL-based loop by the compiler/assembler). Ported verbatim from PicoDrive's DIV1
    /// (cpu/sh2/mame/sh2.c:627-716) rather than re-derived — see this file's own remarks.</summary>
    private int ExecuteDiv1(int m, int n)
    {
        uint oldQ = FlagQ ? 1u : 0u;
        FlagQ = (R[n] & 0x8000_0000) != 0;
        R[n] = (R[n] << 1) | (FlagT ? 1u : 0u);

        uint oldN = R[n];
        if (oldQ == 0)
        {
            if (!FlagM)
            {
                R[n] = unchecked(R[n] - R[m]);
                if (!FlagQ) FlagQ = R[n] > oldN;
                else FlagQ = !(R[n] > oldN);
            }
            else
            {
                R[n] = unchecked(R[n] + R[m]);
                if (!FlagQ) FlagQ = !(R[n] < oldN);
                else FlagQ = R[n] < oldN;
            }
        }
        else
        {
            if (!FlagM)
            {
                R[n] = unchecked(R[n] + R[m]);
                if (!FlagQ) FlagQ = R[n] < oldN;
                else FlagQ = !(R[n] < oldN);
            }
            else
            {
                R[n] = unchecked(R[n] - R[m]);
                if (!FlagQ) FlagQ = !(R[n] > oldN);
                else FlagQ = R[n] > oldN;
            }
        }

        FlagT = FlagQ == FlagM;
        return 1;
    }
}
