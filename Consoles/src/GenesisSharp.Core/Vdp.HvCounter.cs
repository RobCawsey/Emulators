namespace GenesisSharp.Core;

/// <summary>H-counter/V-counter — what games poll (via the HV counter port, 0xC00008) or
/// derive raster/split effects from between H and V interrupts.
///
/// The V-counter's non-linear "jump" modeled here is real, well-documented hardware behavior
/// for NTSC's 224-line (V28) mode: 262 physical scanlines don't fit linearly into an 8-bit
/// counter, so the hardware reuses part of the range, jumping backwards from 0xEA to 0xE5
/// partway through vertical blanking. Only that one NTSC/V28 case is modeled — PAL and the
/// 240-line (V30) mode aren't supported anywhere else in this emulator either.
///
/// The H-counter has a real equivalent jump, confirmed against genesis-plus-gx's
/// <c>core/hvc.h</c> per-master-cycle lookup tables (<c>cycle2hc32</c>/<c>cycle2hc40</c>): the
/// visible byte range isn't a plain 0x00-0xFF wraparound, it skips a block of values partway
/// through the line — H32 counts 0x00-0x93, then jumps straight to 0xE9-0xFF (skipping
/// 0x94-0xE8); H40 counts 0x00-0xB6, then jumps to 0xE4-0xFF (skipping 0xB7-0xE3).
/// <see cref="HorizontalCounter"/> reproduces that same visible-value set and jump, proportionally
/// spread across <see cref="ScanlineProgress"/>'s dot index. This is deliberately *not* a literal
/// port of genesis-plus-gx's 3420-entries-per-line master-cycle tables — H40's real dot clock
/// alternates between two different rates within a line (EDCLK, confirmed in the same source), so
/// the true per-dot repeat pattern isn't uniform, and reproducing it bit-exactly at this emulator's
/// much coarser per-scanline cycle-budget granularity would require either that literal table or a
/// master-clock-accurate timing model this emulator doesn't have. What's implemented instead is an
/// independently-authored proportional mapping onto the *correct* set of visible HC byte values —
/// hardware-accurate at the start/end of the line and the location of the jump, monotonic
/// throughout, but not independently verified dot-for-dot against real hardware breakpoints.</summary>
public sealed partial class Vdp
{
    private const int H32TotalDotsPerLine = 342;
    private const int H40TotalDotsPerLine = 420;

    // Confirmed against genesis-plus-gx's cycle2hc32/cycle2hc40 (core/hvc.h): the last HC value
    // before the jump, and the first HC value after it, for each mode.
    private const int H32LastVisibleBeforeGap = 0x93;
    private const int H32FirstVisibleAfterGap = 0xE9;
    private const int H40LastVisibleBeforeGap = 0xB6;
    private const int H40FirstVisibleAfterGap = 0xE4;

    private int TotalDotsPerLine => Is40CellMode ? H40TotalDotsPerLine : H32TotalDotsPerLine;

    private int HorizontalDotPosition
    {
        get
        {
            int dots = (int)(ScanlineProgress * TotalDotsPerLine);
            return dots < 0 ? 0 : dots > TotalDotsPerLine - 1 ? TotalDotsPerLine - 1 : dots;
        }
    }

    public int HorizontalCounter
    {
        get
        {
            int lastBeforeGap = Is40CellMode ? H40LastVisibleBeforeGap : H32LastVisibleBeforeGap;
            int firstAfterGap = Is40CellMode ? H40FirstVisibleAfterGap : H32FirstVisibleAfterGap;
            int countBeforeGap = lastBeforeGap + 1;
            int countAfterGap = 256 - firstAfterGap;
            int totalVisibleValues = countBeforeGap + countAfterGap;

            int sequenceIndex = HorizontalDotPosition * totalVisibleValues / TotalDotsPerLine;
            if (sequenceIndex >= totalVisibleValues) sequenceIndex = totalVisibleValues - 1;

            return sequenceIndex < countBeforeGap ? sequenceIndex : firstAfterGap + (sequenceIndex - countBeforeGap);
        }
    }

    public bool IsInHorizontalBlank => HorizontalDotPosition >= ActiveWidth;

    /// <summary>NTSC/V28 counter jump: scanlines 0-0xEA map straight through; scanlines
    /// 0xEB-261 (the back half of vertical blanking) jump to 0xE5-0xFF.</summary>
    public int VerticalCounter => CurrentScanline <= 0xEA ? CurrentScanline : CurrentScanline - 6;

    /// <summary>The HV counter port's word value: V-counter in the high byte, H-counter in
    /// the low byte.</summary>
    public ushort ReadHvCounter() => (ushort)(((VerticalCounter & 0xFF) << 8) | (HorizontalCounter & 0xFF));
}
