using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpSpriteTests
{
    private static Vdp CreateVdp()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable
        vdp.Registers[2] = 0x08; // Plane A base = 0x2000
        vdp.Registers[4] = 2;    // Plane B base = 0x4000
        vdp.Registers[5] = 0;    // Sprite table base = 0
        vdp.Registers[13] = 4;   // H-scroll table base = 0x1000
        return vdp;
    }

    private static void WriteSpriteEntry(Vdp vdp, int index, int y, int link, int heightCells, int widthCells, int tileIndex, bool flipH, bool flipV, int paletteLine, bool priority, int x)
    {
        uint address = vdp.SpriteTableBase + (uint)(index * 8);
        ushort word0 = (ushort)((y + 128) & 0x03FF);
        ushort word1 = (ushort)((link & 0x7F) | (((heightCells - 1) & 0x3) << 8) | (((widthCells - 1) & 0x3) << 10));
        ushort word2 = (ushort)((tileIndex & 0x07FF) | (flipH ? 0x0800 : 0) | (flipV ? 0x1000 : 0) | ((paletteLine & 0x3) << 13) | (priority ? 0x8000 : 0));
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

    [Fact]
    public void SingleCellSprite_RendersAtItsPosition()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 10, link: 0, heightCells: 1, widthCells: 1, tileIndex: 2, flipH: false, flipV: false, paletteLine: 1, priority: false, x: 20);
        FillTile(vdp, 2, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x00E0; // pure green

        vdp.RenderScanline(10);

        int offset = (10 * Vdp.ScreenWidth + 20) * 3;
        Assert.Equal(0, vdp.FrameBuffer[offset]);
        Assert.Equal(255, vdp.FrameBuffer[offset + 1]);
        Assert.Equal(0, vdp.FrameBuffer[offset + 2]);
    }

    [Fact]
    public void MultiCellSprite_UsesColumnMajorSubTileOrder()
    {
        var vdp = CreateVdp();
        // 2x2-cell sprite at (0,0), base tile 10: column-major means tile 12 is (col=1,row=0).
        WriteSpriteEntry(vdp, 0, y: 0, link: 0, heightCells: 2, widthCells: 2, tileIndex: 10, flipH: false, flipV: false, paletteLine: 0, priority: false, x: 0);
        FillTile(vdp, 12, colorIndex: 7); // only the (col=1,row=0) sub-tile is opaque
        vdp.Cram[7] = 0x0E00; // pure blue

        vdp.RenderScanline(0);

        int leftOffset = (0 * Vdp.ScreenWidth + 0) * 3; // tile 10 (col 0) — left transparent, all-zero tile
        int rightOffset = (0 * Vdp.ScreenWidth + 8) * 3; // tile 12 (col 1) — opaque
        Assert.Equal(0, vdp.FrameBuffer[leftOffset]);
        Assert.Equal(0, vdp.FrameBuffer[leftOffset + 2]);
        Assert.Equal(255, vdp.FrameBuffer[rightOffset + 2]);
    }

    [Fact]
    public void HorizontalFlip_MirrorsAcrossTheWholeSpriteWidth_NotPerTile()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 0, link: 0, heightCells: 2, widthCells: 2, tileIndex: 10, flipH: true, flipV: false, paletteLine: 0, priority: false, x: 0);
        FillTile(vdp, 12, colorIndex: 7); // unflipped, this sub-tile occupies the right half

        vdp.Cram[7] = 0x0E00;
        vdp.RenderScanline(0);

        int leftOffset = (0 * Vdp.ScreenWidth + 0) * 3;
        int rightOffset = (0 * Vdp.ScreenWidth + 8) * 3;
        // Flipped: the opaque sub-tile now shows on the left, transparent on the right.
        Assert.Equal(255, vdp.FrameBuffer[leftOffset + 2]);
        Assert.Equal(0, vdp.FrameBuffer[rightOffset + 2]);
    }

    [Fact]
    public void LowPrioritySprite_IsHiddenBehindHighPriorityPlaneA()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 2, flipH: false, flipV: false, paletteLine: 1, priority: false, x: 0);
        FillTile(vdp, 2, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00; // sprite color: blue

        // Plane A tile 1 at name-table entry (0,0), high priority, palette line 0, color 5.
        vdp.Vram[vdp.PlaneANameTableBase] = 0x80; // priority bit
        vdp.Vram[vdp.PlaneANameTableBase + 1] = 0x01;
        FillTile(vdp, 1, colorIndex: 5);
        vdp.Cram[5] = 0x000E; // plane color: red

        vdp.RenderScanline(0);

        int offset = 0;
        Assert.Equal(255, vdp.FrameBuffer[offset]); // red wins — plane A's high priority beats the sprite
        Assert.Equal(0, vdp.FrameBuffer[offset + 2]);
    }

    [Fact]
    public void HighPrioritySprite_ShowsOverHighPriorityPlaneA()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 2, flipH: false, flipV: false, paletteLine: 1, priority: true, x: 0);
        FillTile(vdp, 2, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00; // sprite color: blue

        vdp.Vram[vdp.PlaneANameTableBase] = 0x80;
        vdp.Vram[vdp.PlaneANameTableBase + 1] = 0x01;
        FillTile(vdp, 1, colorIndex: 5);
        vdp.Cram[5] = 0x000E; // plane color: red

        vdp.RenderScanline(0);

        int offset = 0;
        Assert.Equal(0, vdp.FrameBuffer[offset]);
        Assert.Equal(255, vdp.FrameBuffer[offset + 2]); // blue wins — the sprite is high priority
    }

    [Fact]
    public void OverlappingOpaqueSprites_EarlierInListWinsAndSetsCollisionFlag()
    {
        var vdp = CreateVdp();
        WriteSpriteEntry(vdp, 0, y: 0, link: 1, heightCells: 1, widthCells: 1, tileIndex: 2, flipH: false, flipV: false, paletteLine: 0, priority: false, x: 0);
        WriteSpriteEntry(vdp, 1, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 3, flipH: false, flipV: false, paletteLine: 0, priority: false, x: 0);
        FillTile(vdp, 2, colorIndex: 1);
        FillTile(vdp, 3, colorIndex: 2);
        vdp.Cram[1] = 0x000E; // sprite 0's color: red
        vdp.Cram[2] = 0x0E00; // sprite 1's color: blue

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]); // sprite 0 (earlier in the list) wins
        Assert.Equal(0, vdp.FrameBuffer[2]);
        Assert.Equal(0x0020, vdp.ReadStatusRegister() & 0x0020); // collision flag set
    }

    [Fact]
    public void LinkedList_FollowsLinkField_NotAttributeTableOrder()
    {
        var vdp = CreateVdp();
        // Sprite 0 links straight to sprite 5, skipping 1-4 entirely.
        WriteSpriteEntry(vdp, 0, y: 0, link: 5, heightCells: 1, widthCells: 1, tileIndex: 2, flipH: false, flipV: false, paletteLine: 0, priority: false, x: 0);
        // Sprites 1-4 sit at a Y that would also be visible on line 0, but must be skipped.
        for (int i = 1; i <= 4; i++)
        {
            WriteSpriteEntry(vdp, i, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 3, flipH: false, flipV: false, paletteLine: 0, priority: false, x: 100 + i * 8);
        }
        WriteSpriteEntry(vdp, 5, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 4, flipH: false, flipV: false, paletteLine: 0, priority: false, x: 50);

        FillTile(vdp, 2, colorIndex: 1);
        FillTile(vdp, 3, colorIndex: 1);
        FillTile(vdp, 4, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        vdp.RenderScanline(0);

        int spriteZeroOffset = 0 * 3;
        int spriteFiveOffset = 50 * 3;
        int skippedSpriteOffset = (100 + 8) * 3; // where sprite 1 would have drawn

        Assert.Equal(255, vdp.FrameBuffer[spriteZeroOffset]); // sprite 0 rendered
        Assert.Equal(255, vdp.FrameBuffer[spriteFiveOffset]); // sprite 5 rendered, via the link
        Assert.Equal(0, vdp.FrameBuffer[skippedSpriteOffset]); // sprite 1 never reached — link skipped it
    }

    [Fact]
    public void MoreThanTwentySpritesOnOneLine_StopsDrawingAndSetsOverflow()
    {
        var vdp = CreateVdp();
        FillTile(vdp, 2, colorIndex: 1);
        vdp.Cram[1] = 0x000E;

        for (int i = 0; i < 21; i++)
        {
            int link = i == 20 ? 0 : i + 1;
            WriteSpriteEntry(vdp, i, y: 0, link: link, heightCells: 1, widthCells: 1, tileIndex: 2, flipH: false, flipV: false, paletteLine: 0, priority: false, x: i * 8);
        }

        vdp.RenderScanline(0);

        int sprite20Offset = (20 * 8) * 3; // the 21st sprite (index 20) — past the per-line limit
        Assert.Equal(0, vdp.FrameBuffer[sprite20Offset]);
        Assert.Equal(0x0040, vdp.ReadStatusRegister() & 0x0040); // overflow flag set
    }
}
