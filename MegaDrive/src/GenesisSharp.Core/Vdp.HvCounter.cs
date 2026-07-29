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
/// The H-counter has a real equivalent jump quirk too, but — unlike the V-counter's, which is
/// widely and consistently cited — I don't have confident recall of its exact breakpoints for
/// H32 vs H40, and would rather ship an honest linear approximation than fabricate specific
/// hex values. <see cref="HorizontalCounter"/> is a linear scaling of <see
/// cref="ScanlineProgress"/> across the commonly-cited total dot counts (342 for H32, 420 for
/// H40) — not hardware-exact, but monotonic and good enough to know roughly where in the line
/// the CPU is, which is what <see cref="IsInHorizontalBlank"/> also depends on.</summary>
public sealed partial class Vdp
{
    private const int H32TotalDotsPerLine = 342;
    private const int H40TotalDotsPerLine = 420;

    private int TotalDotsPerLine => Is40CellMode ? H40TotalDotsPerLine : H32TotalDotsPerLine;

    private int HorizontalDotPosition
    {
        get
        {
            int dots = (int)(ScanlineProgress * TotalDotsPerLine);
            return dots < 0 ? 0 : dots > TotalDotsPerLine - 1 ? TotalDotsPerLine - 1 : dots;
        }
    }

    public int HorizontalCounter => HorizontalDotPosition & 0xFF;

    public bool IsInHorizontalBlank => HorizontalDotPosition >= ActiveWidth;

    /// <summary>NTSC/V28 counter jump: scanlines 0-0xEA map straight through; scanlines
    /// 0xEB-261 (the back half of vertical blanking) jump to 0xE5-0xFF.</summary>
    public int VerticalCounter => CurrentScanline <= 0xEA ? CurrentScanline : CurrentScanline - 6;

    /// <summary>The HV counter port's word value: V-counter in the high byte, H-counter in
    /// the low byte.</summary>
    public ushort ReadHvCounter() => (ushort)(((VerticalCounter & 0xFF) << 8) | (HorizontalCounter & 0xFF));
}
