namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    /// <summary>The window plane's name table row length in cells. Unlike plane A/B (whose
    /// width comes from register 16), this tracks the display width mode directly — 64 for
    /// H40, 32 for H32 — rather than being a fixed value. Confirmed against genesis-plus-gx's
    /// <c>vdp_ctrl.c</c> register-3 write handler (window base masked to bits 15-12 in H40 vs.
    /// 15-11 in H32, implying a fixed 64-cell allocation in H40) and <c>vdp_render.c</c>'s window
    /// row-address computation, which shifts by <c>6 + (reg[12] &amp; 1)</c> — i.e. a 64-byte
    /// (32-cell) row in H32 and a 128-byte (64-cell) row in H40, real hardware pre-allocating the
    /// full 64-cell width even though only 40 columns are ever displayed. Independently
    /// corroborated by SGDK's own H40 4KB-alignment requirement for the window base address.</summary>
    private int WindowRowStrideCells => Is40CellMode ? 64 : 32;

    /// <summary>Whichever of the horizontal/vertical split conditions applies, the window
    /// takes over for that whole row (vertical) or that column within a non-window row
    /// (horizontal) — this is what lets registers 17/18 be used independently for a
    /// horizontal band, a vertical band, or combined for a corner rectangle.</summary>
    private bool IsWindowActiveVertically(int screenY)
    {
        int splitRow = WindowVerticalSplitCell * 8;
        return WindowShowsBottom ? screenY >= splitRow : screenY < splitRow;
    }

    private bool IsWindowActiveHorizontally(int screenX)
    {
        int splitCol = WindowHorizontalSplitCell * 8;
        return WindowShowsRight ? screenX >= splitCol : screenX < splitCol;
    }

    /// <summary>Same tile-decode shape as <see cref="GetPlanePixel"/>, but unscrolled — the
    /// window always maps screen coordinates directly onto its name table.</summary>
    private (int ColorIndex, int PaletteLine, bool Priority) GetWindowPixel(int screenX, int screenY)
    {
        int tileX = screenX >> 3;
        int tileY = screenY >> 3;
        int pixelX = screenX & 7;
        int pixelY = screenY & 7;

        int nameTableIndex = tileY * WindowRowStrideCells + tileX;
        uint entryAddress = WindowNameTableBase + (uint)(nameTableIndex * 2);
        ushort entry = ReadVramWord(entryAddress);

        var (tileIndex, paletteLine, flipH, flipV, priority) = DecodeNameTableEntry(entry);

        int finalPixelX = flipH ? 7 - pixelX : pixelX;
        int finalPixelY = flipV ? 7 - pixelY : pixelY;
        (tileIndex, finalPixelY) = ApplyInterlaceTileAddress(tileIndex, finalPixelY);

        uint tileDataAddress = (uint)(tileIndex * 32 + finalPixelY * 4 + finalPixelX / 2);
        byte tileByte = Vram[tileDataAddress & (VramSize - 1)];
        int colorIndex = (finalPixelX % 2 == 0) ? (tileByte >> 4) : (tileByte & 0x0F);

        return (colorIndex, paletteLine, priority);
    }
}
