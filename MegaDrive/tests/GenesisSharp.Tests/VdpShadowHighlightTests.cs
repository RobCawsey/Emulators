using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpShadowHighlightTests
{
    private static Vdp CreateVdp()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable
        vdp.Registers[2] = 0x08; // Plane A base = 0x2000
        vdp.Registers[4] = 2;    // Plane B base = 0x4000 (left transparent)
        vdp.Registers[5] = 0;    // sprite table base = 0
        vdp.Registers[13] = 4;   // H-scroll base = 0x1000 (left zeroed -> no scroll)
        vdp.Registers[12] = 0x08; // shadow/highlight enable
        return vdp;
    }

    private static void WriteSpriteEntry(Vdp vdp, int index, int y, int paletteLine, int colorTileIndex, bool priority, int x)
    {
        uint address = vdp.SpriteTableBase + (uint)(index * 8);
        ushort word0 = (ushort)((y + 128) & 0x03FF);
        ushort word1 = 0; // link=0, 1x1 cell
        ushort word2 = (ushort)((colorTileIndex & 0x07FF) | ((paletteLine & 0x3) << 13) | (priority ? 0x8000 : 0));
        ushort word3 = (ushort)((x + 128) & 0x03FF);

        void WriteWord(uint addr, ushort value)
        {
            vdp.Vram[addr] = (byte)(value >> 8);
            vdp.Vram[addr + 1] = (byte)value;
        }

        WriteWord(address, word0);
        WriteWord(address + 2, word1);
        WriteWord(address + 4, word2);
        WriteWord(address + 6, word3);
    }

    private static void FillTile(Vdp vdp, int tileIndex, byte colorIndex)
    {
        byte b = (byte)((colorIndex << 4) | colorIndex);
        for (int i = 0; i < 32; i++)
        {
            vdp.Vram[tileIndex * 32 + i] = b;
        }
    }

    private static void WritePlaneAEntry(Vdp vdp, int paletteLine, int tileIndex, bool priority)
    {
        ushort entry = (ushort)((tileIndex & 0x07FF) | ((paletteLine & 0x3) << 13) | (priority ? 0x8000 : 0));
        vdp.Vram[vdp.PlaneANameTableBase] = (byte)(entry >> 8);
        vdp.Vram[vdp.PlaneANameTableBase + 1] = (byte)entry;
    }

    [Fact]
    public void ShadowHighlightEnabled_ReflectsRegister12Bit3()
    {
        var vdp = new Vdp();
        Assert.False(vdp.ShadowHighlightEnabled);

        vdp.Registers[12] = 0x08;
        Assert.True(vdp.ShadowHighlightEnabled);
    }

    [Fact]
    public void LowPrioritySprite_DefaultsToShadowBrightness()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 0, paletteLine: 0, colorTileIndex: 2, priority: false, x: 0);
        FillTile(vdp, 2, colorIndex: 5);
        vdp.Cram[5] = 0x000E; // pure red, R=255

        vdp.RenderScanline(0);

        Assert.Equal(127, vdp.FrameBuffer[0]); // 255/2, truncated
    }

    [Fact]
    public void HighPrioritySprite_StaysAtNormalBrightness()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 0, paletteLine: 0, colorTileIndex: 2, priority: true, x: 0);
        FillTile(vdp, 2, colorIndex: 5);
        vdp.Cram[5] = 0x000E;

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]);
    }

    [Fact]
    public void HighlightOperator_BrightensTheUnderlyingLowPriorityPixel()
    {
        var vdp = CreateVdp();
        // Plane A: low priority, palette 0, color 3 -> would default to shadow.
        WritePlaneAEntry(vdp, paletteLine: 0, tileIndex: 3, priority: false);
        FillTile(vdp, 3, colorIndex: 3);
        vdp.Cram[3] = 0x0008; // R channel value 4/7 -> 145

        // Highlight operator sprite at the same position: palette 3, color 14. Never itself visible.
        WriteSpriteEntry(vdp, 0, y: 0, paletteLine: 3, colorTileIndex: 5, priority: false, x: 0);
        FillTile(vdp, 5, colorIndex: 14);

        vdp.RenderScanline(0);

        Assert.Equal(200, vdp.FrameBuffer[0]); // 145 + (255-145)/2, not the default-shadowed 72
    }

    [Fact]
    public void ShadowOperator_DarkensTheUnderlyingHighPriorityPixel()
    {
        var vdp = CreateVdp();
        // Plane A: high priority this time — would normally stay at full (145) brightness.
        WritePlaneAEntry(vdp, paletteLine: 0, tileIndex: 3, priority: true);
        FillTile(vdp, 3, colorIndex: 3);
        vdp.Cram[3] = 0x0008; // R=145

        // Shadow operator sprite: palette 3, color 15.
        WriteSpriteEntry(vdp, 0, y: 0, paletteLine: 3, colorTileIndex: 5, priority: false, x: 0);
        FillTile(vdp, 5, colorIndex: 15);

        vdp.RenderScanline(0);

        Assert.Equal(72, vdp.FrameBuffer[0]); // 145/2, overriding the high-priority normal default
    }

    [Fact]
    public void HighlightOperator_AffectsTheBackdropWhenNothingElseIsVisible()
    {
        var vdp = CreateVdp();
        vdp.Registers[7] = 3; // backdrop: palette line 0, color index 3
        vdp.Cram[3] = 0x0008; // R=145

        WriteSpriteEntry(vdp, 0, y: 0, paletteLine: 3, colorTileIndex: 5, priority: false, x: 0);
        FillTile(vdp, 5, colorIndex: 14);

        vdp.RenderScanline(0);

        Assert.Equal(200, vdp.FrameBuffer[0]);
    }

    [Fact]
    public void OperatorSprite_IsNeverItselfVisible()
    {
        var vdp = CreateVdp();
        vdp.Registers[7] = 0; // backdrop: palette 0, color 0 (black)
        vdp.Cram[3 * 16 + 14] = 0x0E00; // the operator's "own" color: bright blue — must never show

        WriteSpriteEntry(vdp, 0, y: 0, paletteLine: 3, colorTileIndex: 5, priority: true, x: 0);
        FillTile(vdp, 5, colorIndex: 14);

        vdp.RenderScanline(0);

        // Backdrop (black) shows through, highlighted to gray (127,127,127) — not the
        // operator's own blue (which would show B=255, R=G=0).
        Assert.Equal(127, vdp.FrameBuffer[0]);
        Assert.Equal(127, vdp.FrameBuffer[1]);
        Assert.Equal(127, vdp.FrameBuffer[2]);
    }
}
