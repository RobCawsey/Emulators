using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpInterlaceTests
{
    private static void FillTile(Vdp vdp, int tileIndex, byte colorIndex)
    {
        byte b = (byte)((colorIndex << 4) | colorIndex);
        for (int i = 0; i < 32; i++)
        {
            vdp.Vram[tileIndex * 32 + i] = b;
        }
    }

    [Theory]
    [InlineData(0x00, 0, false)]
    [InlineData(0x02, 1, false)]
    [InlineData(0x04, 2, false)]
    [InlineData(0x06, 3, true)]
    public void InterlaceMode_DecodesRegister12Bits1And2(byte reg12, int expectedMode, bool expectedIm2)
    {
        var vdp = new Vdp();
        vdp.Registers[12] = reg12;

        Assert.Equal(expectedMode, vdp.InterlaceMode);
        Assert.Equal(expectedIm2, vdp.IsInterlaceMode2);
    }

    [Fact]
    public void CurrentFieldIsOdd_TogglesOncePerFrame()
    {
        var vdp = new Vdp();
        Assert.False(vdp.CurrentFieldIsOdd);

        for (int i = 0; i < Vdp.LinesPerFrame; i++)
        {
            vdp.AdvanceScanline();
        }
        Assert.True(vdp.CurrentFieldIsOdd);

        for (int i = 0; i < Vdp.LinesPerFrame; i++)
        {
            vdp.AdvanceScanline();
        }
        Assert.False(vdp.CurrentFieldIsOdd);
    }

    /// <summary>Fills one row (4 bytes) of a tile with a uniform color index, leaving the
    /// tile's other rows untouched — lets a test give each row of a tile a distinct, separately
    /// identifiable marker.</summary>
    private static void FillTileRow(Vdp vdp, int tileIndex, int row, byte colorIndex)
    {
        byte b = (byte)((colorIndex << 4) | colorIndex);
        uint rowBase = (uint)(tileIndex * 32 + row * 4);
        for (int i = 0; i < 4; i++)
        {
            vdp.Vram[rowBase + i] = b;
        }
    }

    /// <summary>Marker palette used throughout this file's row-level IM2 tests: color indices
    /// 1-4 map to distinct, easily distinguished red-channel levels (r3 = 1..4).</summary>
    private static void SetUpMarkerPalette(Vdp vdp)
    {
        for (int r3 = 1; r3 <= 4; r3++)
        {
            vdp.Cram[r3] = (ushort)(r3 << 1);
        }
    }

    private static readonly int[] MarkerRed = { 0, 36, 72, 109, 145 }; // index by colorIndex 0-4

    [Fact]
    public void Im2_PlaneA_TopHalf_SamplesEveryOtherPhysicalRowByField()
    {
        // Logical tile index 5 -> pair (10, 11); rows 0-3 of this field's 8-row visual slot
        // read physical tile 10 (the top half of the pair). Leaving tile 11 all zero means a
        // bug that read the wrong tile would show transparent/backdrop, not a wrong marker.
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08;
        vdp.Registers[4] = 2;
        vdp.Registers[13] = 4;
        vdp.Registers[12] = 0x06; // IM2
        SetUpMarkerPalette(vdp);

        vdp.Vram[vdp.PlaneANameTableBase] = 0x00;
        vdp.Vram[vdp.PlaneANameTableBase + 1] = 0x05;
        FillTileRow(vdp, 10, row: 0, colorIndex: 1);
        FillTileRow(vdp, 10, row: 1, colorIndex: 2);
        FillTileRow(vdp, 10, row: 6, colorIndex: 3);
        FillTileRow(vdp, 10, row: 7, colorIndex: 4);

        // Even field: screenY=0 (rowInHalf 0) -> physical row 0*2+0=0 -> marker 1.
        //             screenY=3 (rowInHalf 3) -> physical row 3*2+0=6 -> marker 3.
        vdp.RenderScanline(0);
        Assert.Equal(MarkerRed[1], vdp.FrameBuffer[0]);
        vdp.RenderScanline(3);
        Assert.Equal(MarkerRed[3], vdp.FrameBuffer[(3 * Vdp.ScreenWidth) * 3]);

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();

        // Odd field: screenY=0 -> physical row 0*2+1=1 -> marker 2.
        //            screenY=3 -> physical row 3*2+1=7 -> marker 4.
        vdp.RenderScanline(0);
        Assert.Equal(MarkerRed[2], vdp.FrameBuffer[0]);
        vdp.RenderScanline(3);
        Assert.Equal(MarkerRed[4], vdp.FrameBuffer[(3 * Vdp.ScreenWidth) * 3]);
    }

    [Fact]
    public void Im2_PlaneA_BottomHalf_SamplesEveryOtherPhysicalRowByField()
    {
        // Same as above but exercising screenY 4-7 (rowInHalf's other half), which reads
        // physical tile 11 -- the pair's bottom half. Tile 10 is left all zero this time.
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08;
        vdp.Registers[4] = 2;
        vdp.Registers[13] = 4;
        vdp.Registers[12] = 0x06; // IM2
        SetUpMarkerPalette(vdp);

        vdp.Vram[vdp.PlaneANameTableBase] = 0x00;
        vdp.Vram[vdp.PlaneANameTableBase + 1] = 0x05;
        FillTileRow(vdp, 11, row: 0, colorIndex: 1);
        FillTileRow(vdp, 11, row: 1, colorIndex: 2);
        FillTileRow(vdp, 11, row: 6, colorIndex: 3);
        FillTileRow(vdp, 11, row: 7, colorIndex: 4);

        vdp.RenderScanline(4);
        Assert.Equal(MarkerRed[1], vdp.FrameBuffer[(4 * Vdp.ScreenWidth) * 3]);
        vdp.RenderScanline(7);
        Assert.Equal(MarkerRed[3], vdp.FrameBuffer[(7 * Vdp.ScreenWidth) * 3]);

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();

        vdp.RenderScanline(4);
        Assert.Equal(MarkerRed[2], vdp.FrameBuffer[(4 * Vdp.ScreenWidth) * 3]);
        vdp.RenderScanline(7);
        Assert.Equal(MarkerRed[4], vdp.FrameBuffer[(7 * Vdp.ScreenWidth) * 3]);
    }

    [Fact]
    public void Im2_TileIndexAtOrAboveOneThousandTwentyFour_MasksToLowTenBitsBeforePairing()
    {
        // Regression test for a real bug the old tileIndex*2+parity formula had: a logical
        // tile index >= 1024 (bit 10 set) would double past VRAM's valid tile range. The
        // pattern name's top bit must be dropped before pairing, so tile index 1025 behaves
        // identically to tile index 1 (pair -> physical tiles 2/3, not 2050/2051).
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08;
        vdp.Registers[4] = 2;
        vdp.Registers[13] = 4;
        vdp.Registers[12] = 0x06; // IM2
        SetUpMarkerPalette(vdp);

        ushort entry = 1025; // 0x401 -- bit 10 set
        vdp.Vram[vdp.PlaneANameTableBase] = (byte)(entry >> 8);
        vdp.Vram[vdp.PlaneANameTableBase + 1] = (byte)entry;
        FillTileRow(vdp, 2, row: 0, colorIndex: 1); // physical tile 2 = (1025 & 0x3FF) * 2

        vdp.RenderScanline(0); // even field -> physical row 0

        Assert.Equal(MarkerRed[1], vdp.FrameBuffer[0]);
    }

    [Fact]
    public void Im2_VerticalFlip_ReversesWhichHalfAndRowAreSampled()
    {
        // Flip is resolved before the IM2 split, not after: a vertically-flipped tile's
        // screenY=0 (which normally reads the pair's top half) should instead read from the
        // bottom half, at a row derived from the flipped (not raw) position.
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08;
        vdp.Registers[4] = 2;
        vdp.Registers[13] = 4;
        vdp.Registers[12] = 0x06; // IM2
        SetUpMarkerPalette(vdp);

        ushort entry = (ushort)(0x1000 | 5); // flipV bit + logical tile index 5
        vdp.Vram[vdp.PlaneANameTableBase] = (byte)(entry >> 8);
        vdp.Vram[vdp.PlaneANameTableBase + 1] = (byte)entry;
        // Flipped, screenY=0 -> raw pixelY=0 -> finalPixelY=7-0=7 -> half=1 (bottom, tile 11),
        // rowInHalf=3, physical row=3*2+0=6 (even field).
        FillTileRow(vdp, 11, row: 6, colorIndex: 1);

        vdp.RenderScanline(0);

        Assert.Equal(MarkerRed[1], vdp.FrameBuffer[0]);
    }

    [Fact]
    public void Im2_Window_BottomHalf_SamplesEveryOtherPhysicalRowByField()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[3] = 0x20; // window base = 0x8000
        vdp.Registers[18] = 0x80; // window covers every row
        vdp.Registers[12] = 0x06; // IM2
        SetUpMarkerPalette(vdp);

        vdp.Vram[vdp.WindowNameTableBase] = 0x00;
        vdp.Vram[vdp.WindowNameTableBase + 1] = 0x05;
        FillTileRow(vdp, 11, row: 0, colorIndex: 1);
        FillTileRow(vdp, 11, row: 1, colorIndex: 2);

        vdp.RenderScanline(4);
        Assert.Equal(MarkerRed[1], vdp.FrameBuffer[(4 * Vdp.ScreenWidth) * 3]);

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();

        vdp.RenderScanline(4);
        Assert.Equal(MarkerRed[2], vdp.FrameBuffer[(4 * Vdp.ScreenWidth) * 3]);
    }

    [Fact]
    public void Im2_Sprite_BottomHalf_SamplesEveryOtherPhysicalRowByField()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08; // Plane A base — kept transparent (no entry written)
        vdp.Registers[13] = 4;
        vdp.Registers[5] = 0;    // sprite table base = 0
        vdp.Registers[12] = 0x06; // IM2
        SetUpMarkerPalette(vdp);

        // Sprite entry: Y=138(actual 10), link0, 1x1 cell, tile index 5 (logical), palette0.
        vdp.Vram[0] = 0x00; vdp.Vram[1] = 0x8A; // Y
        vdp.Vram[2] = 0x00; vdp.Vram[3] = 0x00; // link=0, 1x1
        vdp.Vram[4] = 0x00; vdp.Vram[5] = 0x05; // tile index 5
        vdp.Vram[6] = 0x00; vdp.Vram[7] = 0x94; // X (actual 20)
        FillTileRow(vdp, 11, row: 0, colorIndex: 1);
        FillTileRow(vdp, 11, row: 1, colorIndex: 2);

        // Sprite row 4 (scanline 14, since Y=10) -> pixelY=4 -> bottom half (tile 11).
        vdp.RenderScanline(14);
        Assert.Equal(MarkerRed[1], vdp.FrameBuffer[(14 * Vdp.ScreenWidth + 20) * 3]);

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();
        vdp.RenderScanline(14);
        Assert.Equal(MarkerRed[2], vdp.FrameBuffer[(14 * Vdp.ScreenWidth + 20) * 3]);
    }

    [Fact]
    public void NonInterlaceModes_LeaveTileIndexUnchanged()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08;
        vdp.Registers[4] = 2;
        vdp.Registers[13] = 4;
        vdp.Registers[12] = 0x02; // IM1 — no addressing change

        vdp.Vram[vdp.PlaneANameTableBase] = 0x00;
        vdp.Vram[vdp.PlaneANameTableBase + 1] = 0x05; // logical tile 5, used as-is
        FillTile(vdp, 5, colorIndex: 1);
        vdp.Cram[1] = 0x000E;

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]);
    }
}
