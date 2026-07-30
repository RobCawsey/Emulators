namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    public bool HorizontalInterruptEnabled => (Registers[0] & 0x10) != 0;

    public bool DisplayEnabled => (Registers[1] & 0x40) != 0;
    public bool VerticalInterruptEnabled => (Registers[1] & 0x20) != 0;
    public bool DmaEnabled => (Registers[1] & 0x10) != 0;

    /// <summary>Register 1 bit 2 — corrected via real-ROM testing. This was originally
    /// implemented as bit 3, which is actually the (Genesis-specific) 30-cell/240-line select
    /// bit, not the Mode 5 select bit; a real, complete homebrew game's very first register 1
    /// write (0x14: DMA enable + this bit, deliberately leaving bit 3 clear) is what exposed
    /// the mistake — every one of this project's own tests had been unknowingly reinforcing the
    /// wrong bit, since they were all written against the same incorrect assumption.</summary>
    public bool Mode5Enabled => (Registers[1] & 0x04) != 0;

    /// <summary>Mode select is TMS9918-heritage: M4 (register 0 bit 2) enables SMS-style
    /// modes at all, and M5 (<see cref="Mode5Enabled"/>) then picks between Genesis-native
    /// Mode 5 (M4=1,M5=1) and SMS-compatible Mode 4 (M4=1,M5=0) — M5 wins outright when set,
    /// regardless of M4 (real hardware convention leaves M4 set even in Mode 5). M4=0 selects
    /// the even-older TMS9918 text/graphics modes, which aren't implemented — vanishingly
    /// unlikely to matter for any real Genesis or SMS software.</summary>
    public bool Mode4Enabled => (Registers[0] & 0x04) != 0 && !Mode5Enabled;

    public uint PlaneANameTableBase => (uint)(Registers[2] & 0x38) << 10;
    public uint WindowNameTableBase => (uint)(Registers[3] & 0x3E) << 10;
    public uint PlaneBNameTableBase => (uint)(Registers[4] & 0x07) << 13;
    public uint SpriteTableBase => (uint)(Registers[5] & 0x7F) << 9;
    public uint HScrollTableBase => (uint)(Registers[13] & 0x3F) << 10;

    public int BackgroundPaletteLine => (Registers[7] >> 4) & 0x03;
    public int BackgroundColorIndex => Registers[7] & 0x0F;

    public int HInterruptCounter => Registers[10];

    /// <summary>false = full-screen vertical scroll, true = per-2-column-pair.</summary>
    public bool VerticalScrollIsPerColumn => (Registers[11] & 0x04) != 0;

    /// <summary>H40 (320px, 40 cells) when set; H32 (256px, 32 cells) otherwise.</summary>
    public bool Is40CellMode => (Registers[12] & 0x01) != 0;

    /// <summary>The number of visible columns for the current display width — what <see
    /// cref="Vdp.RenderScanline"/> actually draws each line. <see cref="Vdp.ScreenWidth"/>
    /// stays fixed at 320 (H40's width) since that's the frame buffer's allocated size; in
    /// H32 only the first 256 of those columns are written per line, with the rest blanked.</summary>
    public int ActiveWidth => Is40CellMode ? 320 : 256;

    public byte AutoIncrement => Registers[15];

    /// <summary>Register 17: horizontal split point for the window plane, in 2-cell (16px)
    /// units — doubled because 5 bits alone (0-31) can't reach all 40 H40 columns otherwise.
    /// <see cref="WindowShowsRight"/> false = window occupies columns left of the split,
    /// true = right of it.</summary>
    public int WindowHorizontalSplitCell => (Registers[17] & 0x1F) * 2;
    public bool WindowShowsRight => (Registers[17] & 0x80) != 0;

    /// <summary>Register 18: vertical split point for the window plane, in 1-cell (8px)
    /// units (5 bits already covers all 32 rows, so no doubling here). <see
    /// cref="WindowShowsBottom"/> false = window occupies rows above the split, true = below.</summary>
    public int WindowVerticalSplitCell => Registers[18] & 0x1F;
    public bool WindowShowsBottom => (Registers[18] & 0x80) != 0;

    private static int DecodePlaneDimensionTiles(int bits) => bits switch { 0 => 32, 1 => 64, 3 => 128, _ => 32 };

    public int PlaneWidthTiles => DecodePlaneDimensionTiles(Registers[16] & 0x03);
    public int PlaneHeightTiles => DecodePlaneDimensionTiles((Registers[16] >> 4) & 0x03);

    public ushort DmaLength => (ushort)(Registers[19] | (Registers[20] << 8));

    /// <summary>DMA source address in 68000 memory, for the memory-to-VDP copy mode — the
    /// register pair holds a *word* address, hence the final &lt;&lt;1 to get a byte address.
    /// Masked to the low 7 bits of register 23, not 6 -- only bit 7 is <see
    /// cref="CurrentDmaMode"/>'s real selector (its own switch already treats 0x00 and 0x40
    /// identically as MemoryToVdp); bit 6 is real hardware's documented DMA source address bit
    /// 23, the one bit needed to reach work RAM ($FF0000+) at all. Masking it out (as an
    /// earlier version of this property did, on the mistaken assumption that both top bits were
    /// reserved for the mode selector) capped every reconstructed source address at $7FFFFE --
    /// any RAM-sourced transfer silently read from wherever that wrong, ROM/open-bus address
    /// happened to fall instead, rather than the real source buffer. Confirmed via a real ROM
    /// (Altered Beast) whose per-column background-tile DMA reads a RAM staging buffer at
    /// $FFF000+ but, under the old mask, kept computing $7EF000+ -- open bus, reading back all
    /// $FF and writing a blank tile into the nametable every time.</summary>
    public uint DmaSourceAddress => (uint)(Registers[21] | (Registers[22] << 8) | ((Registers[23] & 0x7F) << 16)) << 1;

    public DmaMode CurrentDmaMode => (Registers[23] & 0xC0) switch
    {
        0x80 => DmaMode.VramFill,
        0xC0 => DmaMode.VramCopy,
        _ => DmaMode.MemoryToVdp,
    };
}

public enum DmaMode
{
    MemoryToVdp,
    VramFill,
    VramCopy,
}
