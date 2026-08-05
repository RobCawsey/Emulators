namespace GenesisSharp.Core;

/// <summary>The Genesis VDP (Video Display Processor) — Mode 5 only, H40 (320px) only,
/// non-interlaced. Register and command-word bit layouts below are reproduced from general
/// Sega Genesis VDP documentation (the kind widely summarized in community references like
/// Charles MacDonald's genvdp.txt) recalled from training, not cross-checked against an
/// authoritative source or real hardware the way the 68000/Z80 opcode encodings were —
/// treat the exact bit positions as a reasonable first draft to verify, not a settled fact.</summary>
public sealed partial class Vdp
{
    public const int VramSize = 0x10000;
    public const int CramColorCount = 64;
    public const int VsramSize = 40; // 40 words — vertical scroll value per 2-column pair

    public const int ScreenWidth = 320;
    public const int ScreenHeight = 224;
    public const int LinesPerFrame = 262; // NTSC: 224 active + 38 blanking

    public byte[] Vram { get; } = new byte[VramSize];
    public ushort[] Cram { get; } = new ushort[CramColorCount];
    public ushort[] Vsram { get; } = new ushort[VsramSize];
    public byte[] Registers { get; } = new byte[24];

    /// <summary>Lets DMA's "68000 memory to VDP" copy mode reach cartridge ROM / work RAM
    /// without the VDP needing a reference to the whole bus. GenesisSharp.Core wires this to
    /// its own address decode.</summary>
    public Func<uint, byte>? ExternalMemoryRead { get; set; }

    /// <summary>Fired once per frame at the start of vertical blank, but only when <see
    /// cref="VerticalInterruptEnabled"/> is set — GenesisSharp.Core wires this to
    /// <c>GenesisConsole.RequestVerticalBlankInterrupt</c>.</summary>
    public event Action? VerticalBlankStarted;

    /// <summary>Fired once per frame at the exact same edge as <see cref="VerticalBlankStarted"/>,
    /// but unconditionally — not gated by <see cref="VerticalInterruptEnabled"/>. Exists
    /// specifically for the 32X's own VINT, a genuinely separate SH-2-facing interrupt path with
    /// no dependency on whether the 68000 wants its own vblank interrupt at all: confirmed against
    /// PicoDrive's own <c>p32x_start_blank</c> (<c>reference/PicoDrive/picodrive/pico/32x/32x.c:
    /// 316-330</c>), which raises VINT with no check on the 68000's own VDP register 1 IE0 bit
    /// anywhere in it. GenesisSharp.Core wires this to <c>Sega32X.OnVerticalBlankStarted</c>.</summary>
    public event Action? EnteredVBlank;

    /// <summary>Fired every (<see cref="HInterruptCounter"/> + 1) scanlines during the active
    /// display, but only when <see cref="HorizontalInterruptEnabled"/> is set. Real hardware
    /// shares a single 68000 interrupt line (level 4, the same autovector as
    /// <see cref="VerticalBlankStarted"/>) between H and V interrupts — there's no separate
    /// vector for this one.</summary>
    public event Action? HorizontalInterruptRequested;

    public void Reset()
    {
        Array.Clear(Vram);
        Array.Clear(Cram);
        Array.Clear(Vsram);
        Array.Clear(Registers);
        ResetPortState();
        ResetTimingState();
    }
}
