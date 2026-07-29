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
}
