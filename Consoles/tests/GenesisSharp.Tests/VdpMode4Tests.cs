using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpMode4Tests
{
    private static Vdp CreateVdp()
    {
        var vdp = new Vdp();
        vdp.Registers[0] = 0x04; // M4 set
        vdp.Registers[1] = 0x40; // display enable, M5 clear -> Mode 4
        // Name table base = 0x3800 — deliberately away from address 0, where tile 0's data
        // (the default name-table entry everywhere) otherwise lives; the two would collide
        // and each stomp on the other's bytes if the table were left at its default base 0.
        vdp.Registers[2] = 0x0E;
        // Sprite attribute table base = 0x100, away from tile data at address 0 -- and, more
        // importantly, explicitly terminated with the 0xD0 sentinel as its very first byte. An
        // *untouched* sprite table isn't actually empty: a zero Y-byte isn't the sentinel, so a
        // background-only test that never writes sprite data would otherwise get 64 phantom
        // "active" sprites at Y=1 (spanning scanlines 1-8), all sharing X=0/tile=0 from the same
        // untouched VRAM. That's invisible to any test that only ever renders scanline 0 (every
        // sprite starts at Y>=1), which is why this went unnoticed until a background test needed
        // to check a later scanline. Tests that intentionally exercise real sprites overwrite
        // this sentinel with their own Y-bytes (typically after moving the table back to base 0).
        vdp.Registers[5] = 2;
        vdp.Vram[vdp.Mode4SpriteAttributeTableBase] = 0xD0;
        return vdp;
    }

    /// <summary>Fills all 8 rows of a tile with a uniform color index, in Mode 4's planar
    /// format (one byte per bitplane per row, MSB = leftmost pixel).</summary>
    private static void FillMode4Tile(Vdp vdp, int tileIndex, int colorIndex)
    {
        for (int row = 0; row < 8; row++)
        {
            uint rowBase = (uint)(tileIndex * 32 + row * 4);
            for (int plane = 0; plane < 4; plane++)
            {
                vdp.Vram[rowBase + plane] = ((colorIndex >> plane) & 1) != 0 ? (byte)0xFF : (byte)0x00;
            }
        }
    }

    [Fact]
    public void ModeSelect_RequiresM4SetAndM5Clear()
    {
        var vdp = new Vdp();

        vdp.Registers[0] = 0x04; vdp.Registers[1] = 0x00;
        Assert.True(vdp.Mode4Enabled);

        vdp.Registers[1] = 0x04; // M5 set -> Mode 5, not Mode 4
        Assert.False(vdp.Mode4Enabled);

        vdp.Registers[0] = 0x00; vdp.Registers[1] = 0x00; // M4 clear
        Assert.False(vdp.Mode4Enabled);
    }

    [Fact]
    public void Background_DecodesPlanarTileFormat()
    {
        var vdp = CreateVdp();
        // Name table entry (tileX=0,tileY=0) at address 0 defaults to tile 0, which is exactly
        // what FillMode4Tile below writes — no explicit entry write needed.
        FillMode4Tile(vdp, 0, colorIndex: 5);
        vdp.Cram[5] = 0x00E0; // palette line 0, color 5: green

        vdp.RenderScanline(0);

        Assert.Equal(0, vdp.FrameBuffer[0]);
        Assert.Equal(255, vdp.FrameBuffer[1]);
        Assert.Equal(0, vdp.FrameBuffer[2]);
    }

    [Fact]
    public void LeftColumnScrollLock_KeepsFirstEightPixelsFixed()
    {
        var vdp = CreateVdp();
        vdp.Registers[0] |= 0x40; // lock left column
        vdp.Registers[8] = 8; // hscroll = 8px

        FillMode4Tile(vdp, 0, colorIndex: 7); // tile 0 (default name-table entry everywhere)
        vdp.Cram[7] = 0x000E; // red

        vdp.Vram[vdp.Mode4NameTableBase + 2] = 0x00; vdp.Vram[vdp.Mode4NameTableBase + 3] = 0x01; // tileX=1 entry -> tile 1
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[3] = 0x0E00; // blue

        vdp.RenderScanline(0);

        // Locked: columns 0-7 ignore hscroll, worldX = x directly -> tile 0 (red).
        Assert.Equal(255, vdp.FrameBuffer[0]);
        Assert.Equal(0, vdp.FrameBuffer[2]);
        // Unlocked: column 8 scrolls by -8 -> worldX = 0 -> tile 0 again (red), not tile 1.
        Assert.Equal(255, vdp.FrameBuffer[8 * 3]);
        Assert.Equal(0, vdp.FrameBuffer[8 * 3 + 2]);
        // Tile 1's content (blue) is now reached at worldX=8 -> screenX = 8 + 8 = 16.
        Assert.Equal(255, vdp.FrameBuffer[16 * 3 + 2]);
    }

    [Fact]
    public void Sprite_UsesYPlusOneOffset()
    {
        var vdp = CreateVdp();
        vdp.Registers[5] = 0; // sprite table base = 0

        vdp.Vram[0] = 9; // sprite 0's Y byte -> actual Y = 10
        vdp.Vram[64] = 20; // sprite 0's X
        vdp.Vram[65] = 1;  // sprite 0's tile index
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00; // sprite palette (line 1), color 3: blue

        vdp.RenderScanline(9);  // one line above the sprite — not visible yet
        vdp.RenderScanline(10); // sprite's first visible row

        Assert.Equal(0, vdp.FrameBuffer[(9 * Vdp.ScreenWidth + 20) * 3 + 2]);
        Assert.Equal(255, vdp.FrameBuffer[(10 * Vdp.ScreenWidth + 20) * 3 + 2]);
    }

    [Fact]
    public void Sprite_YByteSentinel_StopsProcessingEarlier()
    {
        var vdp = CreateVdp();
        vdp.Registers[5] = 0;

        vdp.Vram[0] = 9; // sprite 0: visible at Y=10
        vdp.Vram[64] = 20; vdp.Vram[65] = 1;
        vdp.Vram[1] = 0xD0; // sprite 1: sentinel — stop here
        vdp.Vram[2] = 9; // sprite 2: would also be visible at Y=10, but never reached
        vdp.Vram[68] = 100; vdp.Vram[69] = 1; // sprite 2's X/tile (offset 64 + 2*2)
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00;

        vdp.RenderScanline(10);

        Assert.Equal(255, vdp.FrameBuffer[(10 * Vdp.ScreenWidth + 20) * 3 + 2]); // sprite 0 drawn
        Assert.Equal(0, vdp.FrameBuffer[(10 * Vdp.ScreenWidth + 100) * 3 + 2]); // sprite 2 never reached
    }

    [Fact]
    public void Sprite_MoreThanEightOnOneLine_StopsDrawingAndSetsOverflow()
    {
        var vdp = CreateVdp();
        vdp.Registers[5] = 0;
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00;

        for (int i = 0; i < 9; i++)
        {
            vdp.Vram[i] = 9; // Y byte -> actual Y = 10, all visible on the same line
            vdp.Vram[64 + i * 2] = (byte)(i * 8);
            vdp.Vram[64 + i * 2 + 1] = 1;
        }

        vdp.RenderScanline(10);

        int ninthSpriteOffset = (10 * Vdp.ScreenWidth + 8 * 8) * 3;
        Assert.Equal(0, vdp.FrameBuffer[ninthSpriteOffset + 2]);
        Assert.Equal(0x0040, vdp.ReadStatusRegister() & 0x0040); // overflow flag set
    }

    [Fact]
    public void Backdrop_ComesFromSpritePaletteNotBackgroundPalette()
    {
        var vdp = CreateVdp();
        vdp.Registers[7] = 0x05; // backdrop color index 5 (within the sprite palette)
        vdp.Cram[0 * 16 + 5] = 0x000E; // background palette, index 5: red (should NOT be used)
        vdp.Cram[1 * 16 + 5] = 0x0E00; // sprite palette, index 5: blue (should be used)

        vdp.RenderScanline(0); // tile 0 defaults to all-zero -> transparent everywhere

        Assert.Equal(0, vdp.FrameBuffer[0]);
        Assert.Equal(255, vdp.FrameBuffer[2]);
    }

    [Fact]
    public void HighPriorityBackground_ShowsOverASprite()
    {
        var vdp = CreateVdp();
        vdp.Registers[5] = 2; // move the sprite table off address 0 so it doesn't clash with the name table entry below
        uint spriteBase = vdp.Mode4SpriteAttributeTableBase; // (2 & 0x7E) << 7 = 256

        // Background tile 0 at (tileX=0,tileY=1) — scanline 10 falls in tile row 1 (worldY=10,
        // tileY=10>>3=1), so that's where the entry needs to be. High priority (bit 12).
        uint bgEntryAddress = vdp.Mode4NameTableBase + (1 * 32 + 0) * 2;
        vdp.Vram[bgEntryAddress] = 0x10; vdp.Vram[bgEntryAddress + 1] = 0x00; // entry = 0x1000: priority bit set, tile 0
        FillMode4Tile(vdp, 0, colorIndex: 2);
        vdp.Cram[2] = 0x000E; // red

        vdp.Vram[spriteBase] = 9; // sprite 0's Y byte
        vdp.Vram[spriteBase + 64] = 0; // sprite 0's X
        vdp.Vram[spriteBase + 65] = 1;
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00; // blue

        vdp.RenderScanline(10);

        Assert.Equal(255, vdp.FrameBuffer[(10 * Vdp.ScreenWidth) * 3]); // red wins: background priority beats the sprite
    }

    [Fact]
    public void Mode4TopRowsScrollLock_KeepsFirstSixteenRowsFixed()
    {
        var vdp = CreateVdp();
        vdp.Registers[0] |= 0x80; // lock top rows (2 tile rows)
        vdp.Registers[9] = 8; // vscroll = 8px

        FillMode4Tile(vdp, 0, colorIndex: 7); // tile 0 (default name-table entry everywhere)
        vdp.Cram[7] = 0x000E; // red

        // (tileX=0, tileY=3) entry -> tile 1: only reachable once vscroll actually applies.
        uint entryAddress = vdp.Mode4NameTableBase + (uint)(3 * 32) * 2;
        vdp.Vram[entryAddress] = 0x00; vdp.Vram[entryAddress + 1] = 0x01;
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[3] = 0x0E00; // blue

        vdp.RenderScanline(0);
        vdp.RenderScanline(16);

        // Locked: row 0 ignores vscroll, worldY = screenY directly -> tile 0 (red).
        Assert.Equal(255, vdp.FrameBuffer[0]);
        Assert.Equal(0, vdp.FrameBuffer[2]);
        // Unlocked: row 16 scrolls by +8 -> worldY = 24 -> tileY = 3 -> tile 1 (blue).
        Assert.Equal(255, vdp.FrameBuffer[(16 * Vdp.ScreenWidth) * 3 + 2]);
    }

    [Fact]
    public void Mode4LargeSprites_UsesSixteenPixelTallSpritesAndForcesEvenTileIndex()
    {
        var vdp = CreateVdp();
        vdp.Registers[1] |= 0x02; // 8x16 sprite mode
        vdp.Registers[5] = 0;

        vdp.Vram[0] = 0; // Y byte -> actual Y = 1
        vdp.Vram[64] = 20; // X
        vdp.Vram[65] = 5; // tile index 5 (odd) -> masked to 4 for the top half
        FillMode4Tile(vdp, 4, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00; // blue: top half (tile 4)
        FillMode4Tile(vdp, 5, colorIndex: 6);
        vdp.Cram[1 * 16 + 6] = 0x000E; // red: bottom half (tile 5)

        vdp.RenderScanline(1);  // sprite row 0 -> top half
        vdp.RenderScanline(9);  // sprite row 8 -> bottom half
        vdp.RenderScanline(17); // one past the 16-row-tall sprite -- no longer visible

        Assert.Equal(255, vdp.FrameBuffer[(1 * Vdp.ScreenWidth + 20) * 3 + 2]); // blue
        Assert.Equal(255, vdp.FrameBuffer[(9 * Vdp.ScreenWidth + 20) * 3]); // red
        Assert.Equal(0, vdp.FrameBuffer[(17 * Vdp.ScreenWidth + 20) * 3]);
        Assert.Equal(0, vdp.FrameBuffer[(17 * Vdp.ScreenWidth + 20) * 3 + 2]);
    }

    [Fact]
    public void SpriteCollision_SetsCollisionFlagWhenTwoOpaquePixelsOverlap()
    {
        var vdp = CreateVdp();
        vdp.Registers[5] = 0;
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[1 * 16 + 3] = 0x0E00;

        // Two sprites, same Y and X -> their opaque pixels land on the same screen position.
        vdp.Vram[0] = 9; vdp.Vram[64] = 20; vdp.Vram[65] = 1; // sprite 0
        vdp.Vram[1] = 9; vdp.Vram[66] = 20; vdp.Vram[67] = 1; // sprite 1

        vdp.RenderScanline(10);

        Assert.Equal(0x0020, vdp.ReadStatusRegister() & 0x0020);
    }

    [Fact]
    public void BackgroundVerticalScroll_WrapsAtTwoHundredTwentyFourPixels()
    {
        var vdp = CreateVdp();
        vdp.Registers[9] = 223; // vscroll = 223px -- screenY=1 lands exactly on the wrap boundary

        FillMode4Tile(vdp, 0, colorIndex: 7); // tile 0, at tileY=0
        vdp.Cram[7] = 0x000E; // red

        vdp.RenderScanline(1); // worldY = (1 + 223) % 224 = 0 -> wraps back to tileY=0

        Assert.Equal(255, vdp.FrameBuffer[(1 * Vdp.ScreenWidth) * 3]);
    }

    [Fact]
    public void BackgroundHorizontalScroll_WrapsAtTwoHundredFiftySixPixels()
    {
        var vdp = CreateVdp();
        vdp.Registers[8] = 1; // hscroll = 1px -- screenX=0 lands exactly on the wrap boundary

        // (tileX=31, tileY=0) -- the rightmost column of the 256px-wide virtual plane -> tile 1.
        uint entryAddress = vdp.Mode4NameTableBase + 31u * 2;
        vdp.Vram[entryAddress] = 0x00; vdp.Vram[entryAddress + 1] = 0x01;
        FillMode4Tile(vdp, 1, colorIndex: 3);
        vdp.Cram[3] = 0x0E00; // blue

        vdp.RenderScanline(0); // worldX = (0 - 1) & 0xFF = 255 -> tileX = 31

        Assert.Equal(255, vdp.FrameBuffer[2]);
    }

    [Fact]
    public void Background_AppliesHorizontalAndVerticalTileFlip()
    {
        var vdp = CreateVdp();
        // An asymmetric tile: only the raw top-left pixel (pixelX=0, pixelY=0) is lit, color 3.
        for (int plane = 0; plane < 4; plane++)
        {
            vdp.Vram[(uint)plane] = ((3 >> plane) & 1) != 0 ? (byte)0x80 : (byte)0x00;
        }
        vdp.Cram[3] = 0x000E; // red

        // (tileX=0, tileY=0): tile 0, flipH (bit9) and flipV (bit10) both set.
        ushort entry = 0x0600;
        vdp.Vram[vdp.Mode4NameTableBase] = (byte)(entry >> 8);
        vdp.Vram[vdp.Mode4NameTableBase + 1] = (byte)entry;

        vdp.RenderScanline(7); // last row of the tile

        // Flipped both ways, the raw top-left pixel renders at the tile's bottom-right corner.
        Assert.Equal(255, vdp.FrameBuffer[(7 * Vdp.ScreenWidth + 7) * 3]);
        Assert.Equal(0, vdp.FrameBuffer[(7 * Vdp.ScreenWidth + 0) * 3]);
    }

    [Fact]
    public void RenderMode4Scanline_BlanksRowsAtOrBeyondOneNinetyTwo()
    {
        var vdp = CreateVdp();
        FillMode4Tile(vdp, 0, colorIndex: 7); // would render red everywhere if not blanked
        vdp.Cram[7] = 0x000E;

        int offset = 192 * Vdp.ScreenWidth * 3;
        vdp.FrameBuffer[offset] = 0xAB; // known non-black sentinel

        vdp.RenderScanline(192);

        Assert.Equal(0, vdp.FrameBuffer[offset]);
    }

    [Fact]
    public void RenderMode4Scanline_BlanksColumnsAtOrBeyondTwoFiftySix()
    {
        var vdp = CreateVdp();
        FillMode4Tile(vdp, 0, colorIndex: 7); // tile 0 would be opaque red at this column too
        vdp.Cram[7] = 0x000E;

        int offset = 256 * 3;
        vdp.FrameBuffer[offset] = 0xAB; // known non-black sentinel

        vdp.RenderScanline(0);

        Assert.Equal(0, vdp.FrameBuffer[offset]); // column 256+ (H40's extra width) is blanked
    }
}
