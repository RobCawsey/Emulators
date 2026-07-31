using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpWindowTests
{
    private static Vdp CreateVdp()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable
        vdp.Registers[2] = 0x08; // Plane A base = 0x2000
        vdp.Registers[3] = 0x20; // Window base = 0x8000
        vdp.Registers[4] = 2;    // Plane B base = 0x4000 (left empty/transparent)
        vdp.Registers[13] = 4;   // H-scroll table base = 0x1000 (left zeroed -> no scroll)
        return vdp;
    }

    private static void WriteNameTableEntry(Vdp vdp, uint tableBase, int tileX, int tileY, int rowStrideCells, int tileIndex, int paletteLine, bool priority)
    {
        uint address = tableBase + (uint)((tileY * rowStrideCells + tileX) * 2);
        ushort entry = (ushort)((tileIndex & 0x07FF) | ((paletteLine & 0x3) << 13) | (priority ? 0x8000 : 0));
        vdp.Vram[address] = (byte)(entry >> 8);
        vdp.Vram[address + 1] = (byte)entry;
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
    public void TopBand_ShowsWindowAboveSplitAndPlaneABelowIt()
    {
        var vdp = CreateVdp();
        vdp.Registers[18] = 2; // WVP=2 cells (16px), ShowsBottom=false -> window for rows 0-15

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 0, tileY: 0, rowStrideCells: 32, tileIndex: 5, paletteLine: 0, priority: false);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // window color: red

        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 0, tileY: 2, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 6, paletteLine: 0, priority: false);
        FillTile(vdp, 6, colorIndex: 2);
        vdp.Cram[2] = 0x0E00; // plane A color: blue

        vdp.RenderScanline(0); // inside the window band
        vdp.RenderScanline(16); // just below it — row 16 = tile row 2

        Assert.Equal(255, vdp.FrameBuffer[0]); // red: window showing
        Assert.Equal(255, vdp.FrameBuffer[(16 * Vdp.ScreenWidth) * 3 + 2]); // blue: plane A showing
    }

    [Fact]
    public void BottomBand_ShowsPlaneAAboveSplitAndWindowBelowIt()
    {
        var vdp = CreateVdp();
        vdp.Registers[18] = 0x82; // WVP=2, ShowsBottom=true -> window for rows >= 16

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 0, tileY: 2, rowStrideCells: 32, tileIndex: 5, paletteLine: 0, priority: false);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 0, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 6, paletteLine: 0, priority: false);
        FillTile(vdp, 6, colorIndex: 2);
        vdp.Cram[2] = 0x0E00; // blue

        vdp.RenderScanline(0);
        vdp.RenderScanline(16);

        Assert.Equal(255, vdp.FrameBuffer[2]); // blue: plane A showing above the split
        Assert.Equal(255, vdp.FrameBuffer[(16 * Vdp.ScreenWidth) * 3]); // red: window showing at/below the split
    }

    [Fact]
    public void LeftBand_ShowsWindowLeftOfSplitAndPlaneARightOfIt()
    {
        var vdp = CreateVdp();
        vdp.Registers[17] = 2; // WHP=2 -> split at column 4 (32px), ShowsRight=false -> window left of it

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 0, tileY: 0, rowStrideCells: 32, tileIndex: 5, paletteLine: 0, priority: false);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 4, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 6, paletteLine: 0, priority: false);
        FillTile(vdp, 6, colorIndex: 2);
        vdp.Cram[2] = 0x0E00; // blue

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]); // x=0: red (window)
        Assert.Equal(255, vdp.FrameBuffer[32 * 3 + 2]); // x=32: blue (plane A)
    }

    [Fact]
    public void RightBand_ShowsPlaneALeftOfSplitAndWindowRightOfIt()
    {
        var vdp = CreateVdp();
        vdp.Registers[17] = 0x82; // WHP=2 -> split at column 4 (32px), ShowsRight=true

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 4, tileY: 0, rowStrideCells: 32, tileIndex: 5, paletteLine: 0, priority: false);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        WriteNameTableEntry(vdp, vdp.PlaneANameTableBase, tileX: 0, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 6, paletteLine: 0, priority: false);
        FillTile(vdp, 6, colorIndex: 2);
        vdp.Cram[2] = 0x0E00; // blue

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[2]); // x=0: blue (plane A)
        Assert.Equal(255, vdp.FrameBuffer[32 * 3]); // x=32: red (window)
    }

    [Fact]
    public void HighPriorityWindow_ShowsOverHighPriorityPlaneB()
    {
        var vdp = CreateVdp();
        vdp.Registers[18] = 0x80; // WVP=0, ShowsBottom=true -> window covers every row

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 0, tileY: 0, rowStrideCells: 32, tileIndex: 5, paletteLine: 0, priority: true);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // window color: red

        WriteNameTableEntry(vdp, vdp.PlaneBNameTableBase, tileX: 0, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 6, paletteLine: 0, priority: true);
        FillTile(vdp, 6, colorIndex: 2);
        vdp.Cram[2] = 0x0E00; // plane B color: blue

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]); // red — window (high priority) beats plane B (high priority)
    }

    [Fact]
    public void H40Mode_AddressesWindowNameTableWithSixtyFourCellRowStride()
    {
        // Real hardware allocates the window's name table row as 64 cells wide in H40, not 32
        // (confirmed against genesis-plus-gx's vdp_ctrl.c reg-3 write handler and vdp_render.c's
        // "6 + (reg[12]&1)" row-address shift). Every other test in this file runs in the default
        // H32 mode; this is the only one that exercises the H40 stride at all. tileY=1 is what
        // distinguishes the two strides — at tileY=0 both a 32-cell and 64-cell stride produce the
        // same VRAM address, so a bug here would go unnoticed at row 0.
        var vdp = CreateVdp();
        vdp.Registers[12] = 0x01; // H40 (320px) mode
        vdp.Registers[18] = 0x80; // WVP=0, ShowsBottom=true -> window covers every row

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 5, tileY: 1, rowStrideCells: 64, tileIndex: 5, paletteLine: 0, priority: false);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        vdp.RenderScanline(8); // tile row 1

        int x = 5 * 8; // tileX=5 -> screenX 40
        Assert.Equal(255, vdp.FrameBuffer[(8 * Vdp.ScreenWidth + x) * 3]); // red: read via the 64-cell stride
    }

    [Fact]
    public void LowPriorityWindow_IsHiddenBehindHighPriorityPlaneB()
    {
        var vdp = CreateVdp();
        vdp.Registers[18] = 0x80; // window covers every row

        WriteNameTableEntry(vdp, vdp.WindowNameTableBase, tileX: 0, tileY: 0, rowStrideCells: 32, tileIndex: 5, paletteLine: 0, priority: false);
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        WriteNameTableEntry(vdp, vdp.PlaneBNameTableBase, tileX: 0, tileY: 0, rowStrideCells: vdp.PlaneWidthTiles, tileIndex: 6, paletteLine: 0, priority: true);
        FillTile(vdp, 6, colorIndex: 2);
        vdp.Cram[2] = 0x0E00; // blue

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[2]); // blue — plane B (high priority) beats the window (low priority)
    }
}
