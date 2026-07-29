using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpSpriteMaskingTests
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

    private static void WriteSpriteEntry(Vdp vdp, int index, int y, int link, int heightCells, int widthCells, int tileIndex, int paletteLine, bool priority, int x)
    {
        uint address = vdp.SpriteTableBase + (uint)(index * 8);
        ushort word0 = (ushort)((y + 128) & 0x03FF);
        ushort word1 = (ushort)((link & 0x7F) | (((heightCells - 1) & 0x3) << 8) | (((widthCells - 1) & 0x3) << 10));
        ushort word2 = (ushort)((tileIndex & 0x07FF) | ((paletteLine & 0x3) << 13) | (priority ? 0x8000 : 0));
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
    public void MaskSprite_AfterAPrecedingVisibleSprite_HidesAllLaterSpritesOnThatLine()
    {
        var vdp = CreateVdp();
        FillTile(vdp, 2, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        // Sprite 0: normal, visible, at x=0 -> drawn, so spriteCount becomes 1.
        WriteSpriteEntry(vdp, 0, y: 0, link: 1, heightCells: 1, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: 0);
        // Sprite 1: mask sprite (raw X field 0, actual X=-128) — a sprite already preceded it.
        WriteSpriteEntry(vdp, 1, y: 0, link: 2, heightCells: 1, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: -128);
        // Sprite 2: would otherwise be visible at x=50, but comes after the mask sprite.
        WriteSpriteEntry(vdp, 2, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: 50);

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]); // sprite 0 still rendered
        Assert.Equal(0, vdp.FrameBuffer[50 * 3]); // sprite 2 masked out
    }

    [Fact]
    public void MaskSprite_AsFirstVisibleSpriteOnLine_HasNoEffectAndTraversalContinues()
    {
        var vdp = CreateVdp();
        FillTile(vdp, 2, colorIndex: 1);
        vdp.Cram[1] = 0x000E; // red

        // Sprite 0: mask sprite, first in the list and first visible on this line.
        WriteSpriteEntry(vdp, 0, y: 0, link: 1, heightCells: 1, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: -128);
        // Sprite 1: normal, visible, comes after the mask sprite in the list.
        WriteSpriteEntry(vdp, 1, y: 0, link: 0, heightCells: 1, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: 50);

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[50 * 3]); // sprite 1 still rendered — masking had no effect
    }

    [Fact]
    public void Masking_OnlyAppliesToScanlinesTheMaskSpriteItselfCovers()
    {
        var vdp = CreateVdp();
        FillTile(vdp, 2, colorIndex: 1);
        FillTile(vdp, 3, colorIndex: 1); // 2-cell-tall sprites' second sub-tile (column-major: tileIndex+1)
        vdp.Cram[1] = 0x000E; // red

        // Sprite 0: 2 cells tall (16px), covers scanlines 0-15, always drawn first.
        WriteSpriteEntry(vdp, 0, y: 0, link: 1, heightCells: 2, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: 0);
        // Sprite 1: mask sprite, only 1 cell tall (8px) -> only covers scanlines 0-7.
        WriteSpriteEntry(vdp, 1, y: 0, link: 2, heightCells: 1, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: -128);
        // Sprite 2: 2 cells tall, covers scanlines 0-15, would be visible at x=50.
        WriteSpriteEntry(vdp, 2, y: 0, link: 0, heightCells: 2, widthCells: 1, tileIndex: 2, paletteLine: 0, priority: false, x: 50);

        vdp.RenderScanline(0);
        vdp.RenderScanline(10);

        Assert.Equal(0, vdp.FrameBuffer[50 * 3]); // scanline 0: mask sprite applies here -> hidden
        Assert.Equal(255, vdp.FrameBuffer[(10 * Vdp.ScreenWidth + 50) * 3]); // scanline 10: mask sprite doesn't reach here -> visible
    }
}
