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

    [Fact]
    public void Im2_PlaneA_SelectsDifferentTileByField()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable
        vdp.Registers[2] = 0x08; // Plane A base = 0x2000
        vdp.Registers[4] = 2;    // Plane B base = 0x4000 (left transparent)
        vdp.Registers[13] = 4;   // H-scroll base = 0x1000 (left zeroed -> no scroll)
        vdp.Registers[12] = 0x06; // IM2

        // Name table stores logical tile index 5; IM2 doubles it to 10 (even field) / 11 (odd).
        vdp.Vram[vdp.PlaneANameTableBase] = 0x00;
        vdp.Vram[vdp.PlaneANameTableBase + 1] = 0x05;
        FillTile(vdp, 10, colorIndex: 1);
        FillTile(vdp, 11, colorIndex: 2);
        vdp.Cram[1] = 0x000E; // red
        vdp.Cram[2] = 0x00E0; // green

        vdp.RenderScanline(0);
        Assert.Equal(255, vdp.FrameBuffer[0]); // even field: tile 10, red

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();
        vdp.RenderScanline(0);
        Assert.Equal(255, vdp.FrameBuffer[1]); // odd field: tile 11, green
    }

    [Fact]
    public void Im2_Window_SelectsDifferentTileByField()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[3] = 0x20; // window base = 0x8000
        vdp.Registers[18] = 0x80; // window covers every row
        vdp.Registers[12] = 0x06; // IM2

        vdp.Vram[vdp.WindowNameTableBase] = 0x00;
        vdp.Vram[vdp.WindowNameTableBase + 1] = 0x05;
        FillTile(vdp, 10, colorIndex: 1);
        FillTile(vdp, 11, colorIndex: 2);
        vdp.Cram[1] = 0x000E;
        vdp.Cram[2] = 0x00E0;

        vdp.RenderScanline(0);
        Assert.Equal(255, vdp.FrameBuffer[0]);

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();
        vdp.RenderScanline(0);
        Assert.Equal(255, vdp.FrameBuffer[1]);
    }

    [Fact]
    public void Im2_Sprite_SelectsDifferentTileByField()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[2] = 0x08; // Plane A base — kept transparent (no entry written)
        vdp.Registers[13] = 4;
        vdp.Registers[5] = 0;    // sprite table base = 0
        vdp.Registers[12] = 0x06; // IM2

        // Sprite entry: Y=138(actual 10), link0, 1x1 cell, tile index 5 (logical), palette0.
        vdp.Vram[0] = 0x00; vdp.Vram[1] = 0x8A; // Y
        vdp.Vram[2] = 0x00; vdp.Vram[3] = 0x00; // link=0, 1x1
        vdp.Vram[4] = 0x00; vdp.Vram[5] = 0x05; // tile index 5
        vdp.Vram[6] = 0x00; vdp.Vram[7] = 0x94; // X (actual 20)
        FillTile(vdp, 10, colorIndex: 1);
        FillTile(vdp, 11, colorIndex: 2);
        vdp.Cram[1] = 0x000E;
        vdp.Cram[2] = 0x00E0;

        vdp.RenderScanline(10);
        Assert.Equal(255, vdp.FrameBuffer[(10 * Vdp.ScreenWidth + 20) * 3]);

        for (int i = 0; i < Vdp.LinesPerFrame; i++) vdp.AdvanceScanline();
        vdp.RenderScanline(10);
        Assert.Equal(255, vdp.FrameBuffer[(10 * Vdp.ScreenWidth + 20) * 3 + 1]);
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
