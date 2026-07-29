namespace GenesisSharp.Core;

/// <summary>Mode 4 — the Sega Master System's video mode, which the Genesis VDP retains for
/// backward compatibility. Essentially no real Genesis software ever switches into this mode
/// (it exists for SMS-on-Genesis compatibility and VDP test suites), so this has had far less
/// real-world exposure to cross-check against than the Mode 5 renderer — treat everything in
/// this file as an even lower-confidence first draft than the rest of the VDP.
///
/// Mode 4 differs from Mode 5 in almost every particular: a single scrollable background
/// plane instead of A/B, no window plane, 4bpp *planar* tiles (four separate bitplane bytes
/// per row) instead of Mode 5's packed nibbles, a flat 64-entry sprite table with a sentinel
/// terminator instead of a linked list, and a fixed 256x192 active area.</summary>
public sealed partial class Vdp
{
    public const int Mode4ScreenWidth = 256;
    public const int Mode4ScreenHeight = 192;
    public const int Mode4SpritesInTable = 64;
    public const int Mode4MaxSpritesPerLine = 8;

    public uint Mode4NameTableBase => (uint)(Registers[2] & 0x0E) << 10;
    public uint Mode4SpriteAttributeTableBase => (uint)(Registers[5] & 0x7E) << 7;

    /// <summary>false = 8x8 sprites, true = 8x16 (two vertically-stacked tiles; the low bit
    /// of the tile index is forced to 0 for the top half).</summary>
    public bool Mode4LargeSprites => (Registers[1] & 0x02) != 0;

    /// <summary>When set, the leftmost 8 pixels never scroll horizontally regardless of
    /// <see cref="Mode4HScroll"/> — used for status-bar-style UI columns.</summary>
    public bool Mode4LeftColumnScrollLock => (Registers[0] & 0x40) != 0;

    /// <summary>When set, the top 16 pixels (2 tile rows) never scroll vertically regardless
    /// of <see cref="Mode4VScroll"/>.</summary>
    public bool Mode4TopRowsScrollLock => (Registers[0] & 0x80) != 0;

    public byte Mode4HScroll => Registers[8];
    public byte Mode4VScroll => Registers[9];

    /// <summary>Renders one Mode 4 scanline into the same frame buffer the Mode 5 path uses,
    /// blanking whatever's outside the fixed 256x192 active area (both the bottom rows and
    /// the right-hand columns) so a previous frame's content can't linger there.</summary>
    private void RenderMode4Scanline(int scanline)
    {
        if (scanline >= Mode4ScreenHeight)
        {
            int blankOffset = scanline * ScreenWidth * 3;
            Array.Clear(FrameBuffer, blankOffset, ScreenWidth * 3);
            return;
        }

        int vScroll = Mode4TopRowsScrollLock && scanline < 16 ? 0 : Mode4VScroll;
        var spriteLine = EvaluateMode4SpriteLine(scanline);

        for (int x = 0; x < Mode4ScreenWidth; x++)
        {
            int hScroll = Mode4LeftColumnScrollLock && x < 8 ? 0 : Mode4HScroll;
            var bg = GetMode4BackgroundPixel(x, scanline, hScroll, vScroll);
            var s = spriteLine[x];

            // The background's priority bit lets specific tiles draw over sprites — otherwise
            // an opaque sprite wins, then an opaque background pixel, then the backdrop.
            (int ColorIndex, int PaletteLine)? chosen =
                bg.Priority && bg.ColorIndex != 0 ? (bg.ColorIndex, bg.PaletteLine) :
                s.HasValue ? (s.Value.ColorIndex, s.Value.PaletteLine) :
                bg.ColorIndex != 0 ? (bg.ColorIndex, bg.PaletteLine) :
                null;

            // The backdrop color is documented to always come from the *sprite* palette
            // (line 1), selected by register 7's low nibble — not the background palette.
            ushort cramValue = chosen.HasValue
                ? Cram[chosen.Value.PaletteLine * 16 + chosen.Value.ColorIndex]
                : Cram[16 + (Registers[7] & 0x0F)];

            var (r, g, bl) = DecodeColor(cramValue);
            int offset = (scanline * ScreenWidth + x) * 3;
            FrameBuffer[offset] = r;
            FrameBuffer[offset + 1] = g;
            FrameBuffer[offset + 2] = bl;
        }

        if (Mode4ScreenWidth < ScreenWidth)
        {
            int tailOffset = (scanline * ScreenWidth + Mode4ScreenWidth) * 3;
            Array.Clear(FrameBuffer, tailOffset, (ScreenWidth - Mode4ScreenWidth) * 3);
        }
    }

    /// <summary>The background is always a 32x28-tile virtual plane (256x224px) regardless
    /// of the 192-line visible window; vertical scroll wraps within that, horizontal within
    /// the full 256px width.</summary>
    private (int ColorIndex, int PaletteLine, bool Priority) GetMode4BackgroundPixel(int screenX, int screenY, int hScroll, int vScroll)
    {
        int worldX = (screenX - hScroll) & 0xFF;
        int worldY = (screenY + vScroll) % 224;

        int tileX = worldX >> 3;
        int tileY = worldY >> 3;
        int pixelX = worldX & 7;
        int pixelY = worldY & 7;

        int nameTableIndex = tileY * 32 + tileX;
        uint entryAddress = Mode4NameTableBase + (uint)(nameTableIndex * 2);
        ushort entry = ReadVramWord(entryAddress);

        int tileIndex = entry & 0x01FF;
        bool flipH = (entry & 0x0200) != 0;
        bool flipV = (entry & 0x0400) != 0;
        int paletteLine = (entry >> 11) & 1;
        bool priority = (entry & 0x1000) != 0;

        int finalPixelX = flipH ? 7 - pixelX : pixelX;
        int finalPixelY = flipV ? 7 - pixelY : pixelY;

        int colorIndex = DecodeMode4Pixel(tileIndex, finalPixelX, finalPixelY);
        return (colorIndex, paletteLine, priority);
    }

    /// <summary>Mode 4's planar tile format: each 8-pixel row is 4 separate bytes (one per
    /// bit of the color index), 32 bytes per tile total — unlike Mode 5's packed 4-bit
    /// nibbles. Bit 7 of each plane byte is the leftmost pixel.</summary>
    private int DecodeMode4Pixel(int tileIndex, int pixelX, int pixelY)
    {
        uint rowBase = (uint)(tileIndex * 32 + pixelY * 4);
        int bit = 7 - pixelX;
        int colorIndex = 0;

        for (int plane = 0; plane < 4; plane++)
        {
            byte planeByte = Vram[(rowBase + (uint)plane) & (VramSize - 1)];
            if (((planeByte >> bit) & 1) != 0)
            {
                colorIndex |= 1 << plane;
            }
        }

        return colorIndex;
    }

    /// <summary>Mode 4's sprite table is flat, not a linked list: Y coordinates for all 64
    /// sprites come first (offset 0-63 from the table base), byte value 0xD0 there terminates
    /// the active list early; X/tile pairs for all 64 follow (offset 64-191). Y is stored as
    /// (actual top row - 1). Sprites can't flip and all share one palette (line 1).</summary>
    private (int ColorIndex, int PaletteLine)?[] EvaluateMode4SpriteLine(int scanline)
    {
        var line = new (int ColorIndex, int PaletteLine)?[Mode4ScreenWidth];
        int spriteHeight = Mode4LargeSprites ? 16 : 8;
        int drawnCount = 0;

        for (int i = 0; i < Mode4SpritesInTable; i++)
        {
            byte yByte = Vram[(Mode4SpriteAttributeTableBase + (uint)i) & (VramSize - 1)];
            if (yByte == 0xD0)
            {
                break;
            }

            int y = yByte + 1;
            if (scanline < y || scanline >= y + spriteHeight)
            {
                continue;
            }

            if (drawnCount >= Mode4MaxSpritesPerLine)
            {
                _spriteOverflowPending = true;
                break;
            }

            drawnCount++;

            uint xTileAddress = Mode4SpriteAttributeTableBase + 64 + (uint)(i * 2);
            int x = Vram[xTileAddress & (VramSize - 1)];
            int tileIndex = Vram[(xTileAddress + 1) & (VramSize - 1)];
            if (Mode4LargeSprites)
            {
                tileIndex &= 0x01FE;
            }

            int rowInSprite = scanline - y;
            int effectiveTile = tileIndex + (rowInSprite >> 3);
            int pixelY = rowInSprite & 7;

            for (int col = 0; col < 8; col++)
            {
                int screenX = x + col;
                if ((uint)screenX >= Mode4ScreenWidth)
                {
                    continue;
                }

                int colorIndex = DecodeMode4Pixel(effectiveTile, col, pixelY);
                if (colorIndex == 0)
                {
                    continue;
                }

                if (line[screenX].HasValue)
                {
                    _spriteCollisionPending = true;
                    continue;
                }

                line[screenX] = (colorIndex, 1);
            }
        }

        return line;
    }
}
