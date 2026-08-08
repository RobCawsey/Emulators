namespace GenesisSharp.CpuSh2;

public sealed partial class Sh2
{
    // Bit positions confirmed verbatim against PicoDrive's cpu/sh2/mame/sh2pico.c:64-72
    // (identical copy at cpu/sh2/compiler.c:111-115): #define T 0x1, S 0x2, I 0xf0, Q 0x100,
    // M 0x200. This codebase spells them out as named bools/an int property instead of bare
    // single-letter macros, matching the style of Z80.Flags.cs/M68000.Flags.cs, but the bit
    // positions themselves are a direct transcription, not a recollection.
    private const uint FlagTBit = 0x0000_0001;
    private const uint FlagSBit = 0x0000_0002;
    private const uint InterruptMaskField = 0x0000_00F0;
    private const uint FlagQBit = 0x0000_0100;
    private const uint FlagMBit = 0x0000_0200;

    /// <summary>Test/true — read by BT/BF/BT.S/BF.S, written by CMP/TST/DT and several other
    /// instructions as their sole flag output.</summary>
    public bool FlagT { get => (SR & FlagTBit) != 0; set => SetFlag(FlagTBit, value); }

    /// <summary>Saturation mode — changes MAC.W/MAC.L's overflow behavior (Sh2.MacMultiply.cs).</summary>
    public bool FlagS { get => (SR & FlagSBit) != 0; set => SetFlag(FlagSBit, value); }

    /// <summary>Used only by the DIV0S/DIV1 iterative-division algorithm (Sh2.Divide.cs) —
    /// verbatim algorithm confirmed against PicoDrive's DIV0S/DIV1, cpu/sh2/mame/sh2.c:598-716.</summary>
    public bool FlagQ { get => (SR & FlagQBit) != 0; set => SetFlag(FlagQBit, value); }
    public bool FlagM { get => (SR & FlagMBit) != 0; set => SetFlag(FlagMBit, value); }

    /// <summary>I3-I0 — the current interrupt priority mask (0-15). A maskable interrupt only
    /// services if its level exceeds this value; servicing raises it to the serviced level
    /// (confirmed against PicoDrive's sh2_do_irq, cpu/sh2/sh2.c:62: <c>sr = (sr &amp; ~I) |
    /// (level &lt;&lt; 4)</c>). Set to 15 (all masked) on reset.</summary>
    public int InterruptMask
    {
        get => (int)((SR & InterruptMaskField) >> 4);
        set => SR = (SR & ~InterruptMaskField) | (((uint)value & 0xF) << 4);
    }

    private void SetFlag(uint bit, bool value) => SR = value ? (SR | bit) : (SR & ~bit);
}
