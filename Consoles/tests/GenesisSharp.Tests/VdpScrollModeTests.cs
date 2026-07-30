using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpScrollModeTests
{
    private static Vdp CreateVdp()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable
        vdp.Registers[2] = 0;    // Plane A base = 0
        vdp.Registers[4] = 1;    // Plane B base = 0x2000 (left empty/transparent)
        vdp.Registers[13] = 0x10; // H-scroll table base = 0x4000
        return vdp;
    }

    private static void WriteWord(Vdp vdp, uint address, ushort value)
    {
        vdp.Vram[address] = (byte)(value >> 8);
        vdp.Vram[address + 1] = (byte)value;
    }

    private static void WriteNameTableEntry(Vdp vdp, uint tableBase, int tileX, int tileY, int rowStrideCells, int tileIndex)
    {
        uint address = tableBase + (uint)((tileY * rowStrideCells + tileX) * 2);
        WriteWord(vdp, address, (ushort)(tileIndex & 0x07FF));
    }

    private static void FillTile(Vdp vdp, int tileIndex, byte colorIndex)
    {
        byte b = (byte)((colorIndex << 4) | colorIndex);
        for (int i = 0; i < 32; i++)
        {
            vdp.Vram[tileIndex * 32 + i] = b;
        }
    }

    [Fact]
    public void PerRowHorizontalScroll_UsesADifferentValueEveryEightLines()
    {
        var vdp = CreateVdp();
        vdp.Registers[11] = 2; // horizontal scroll mode = per-row (1 tile row = 8 lines)

        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 1, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 5);
        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 1, tileY: 1, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 5);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        // Row 0 (scanlines 0-7): no scroll. Row 1 (scanlines 8-15): scroll = -8 (shifts +8px).
        // Real hardware always uses the per-scanline addressing formula (byte offset =
        // scanline*4) and just masks which bits of the scanline vary the offset -- per-row mode
        // masks off the low 3 bits, so row 1's offset is (8 & 0xF8) << 2 = 32, not a densely
        // packed index*4=4 (confirmed against genesis-plus-gx's hscroll_mask_table).
        WriteWord(vdp, vdp.HScrollTableBase + 0, 0x0000);
        WriteWord(vdp, vdp.HScrollTableBase + 32, unchecked((ushort)-8));

        vdp.RenderScanline(0);
        vdp.RenderScanline(8);

        Assert.Equal(255, vdp.FrameBuffer[(0 * Vdp.ScreenWidth + 8) * 3]); // unscrolled: tile visible at x=8
        Assert.Equal(255, vdp.FrameBuffer[(8 * Vdp.ScreenWidth + 0) * 3]); // scrolled +8px: now visible at x=0
        Assert.Equal(0, vdp.FrameBuffer[(8 * Vdp.ScreenWidth + 8) * 3]);  // and gone from its old spot
    }

    [Fact]
    public void PerLineHorizontalScroll_UsesADifferentValueEveryScanline()
    {
        var vdp = CreateVdp();
        vdp.Registers[11] = 3; // horizontal scroll mode = per-line

        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 1, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 5);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E;

        WriteWord(vdp, vdp.HScrollTableBase + 0 * 4, 0x0000); // scanline 0: no scroll
        WriteWord(vdp, vdp.HScrollTableBase + 1 * 4, unchecked((ushort)-8)); // scanline 1: +8px

        vdp.RenderScanline(0);
        vdp.RenderScanline(1);

        Assert.Equal(255, vdp.FrameBuffer[(0 * Vdp.ScreenWidth + 8) * 3]);
        Assert.Equal(255, vdp.FrameBuffer[(1 * Vdp.ScreenWidth + 0) * 3]);
        Assert.Equal(0, vdp.FrameBuffer[(1 * Vdp.ScreenWidth + 8) * 3]);
    }

    [Fact]
    public void PerColumnVerticalScroll_UsesADifferentValueEveryTwoColumns()
    {
        var vdp = CreateVdp();
        vdp.Registers[11] = 0x04; // vertical scroll mode = per-2-column, horizontal stays full-screen

        // Tile at tileY=1 (world rows 8-15) in both tileX=0 (group 0, x=0-15) and tileX=2
        // (group 1, x=16-31) — vertical scroll doesn't change which tile *column* a given x
        // reads, only hScroll does that, so each column-group needs its own name-table entry.
        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 0, tileY: 1, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 5);
        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 2, tileY: 1, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 5);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E;

        // Group 0 (x=0-15): no scroll — tile shows at its natural rows 8-15.
        // Group 1 (x=16-31): scroll = +8 (worldY = screenY + 8) — tile shifts to rows 0-7.
        vdp.Vsram[0] = 0x0000; // plane A, group 0
        vdp.Vsram[2] = 8;      // plane A, group 1

        vdp.RenderScanline(0);
        vdp.RenderScanline(8);

        Assert.Equal(0, vdp.FrameBuffer[(0 * Vdp.ScreenWidth + 0) * 3]);   // group 0 @ line 0: worldY=0, not yet visible
        Assert.Equal(255, vdp.FrameBuffer[(8 * Vdp.ScreenWidth + 0) * 3]); // group 0 @ line 8: worldY=8, natural position
        Assert.Equal(255, vdp.FrameBuffer[(0 * Vdp.ScreenWidth + 16) * 3]); // group 1 @ line 0: worldY=8, shifted up into view
        Assert.Equal(0, vdp.FrameBuffer[(8 * Vdp.ScreenWidth + 16) * 3]);  // group 1 @ line 8: worldY=16, shifted past the tile
    }
}
