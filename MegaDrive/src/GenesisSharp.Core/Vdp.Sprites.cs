namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    public const int MaxSpritesInTable = 80;
    public int MaxSpritesPerLine => Is40CellMode ? 20 : 16;
    public int MaxSpritePixelsPerLine => Is40CellMode ? 320 : 256;

    /// <summary>One decoded sprite attribute table entry. Bit layout (same confidence
    /// caveat as the rest of the VDP): word 0 = Y (10 bits, offset by 128); word 1 = link
    /// field (bits 0-6, index of the next sprite in the display list) plus vertical size - 1
    /// (bits 8-9) and horizontal size - 1 (bits 10-11), each in 8px cells; word 2 mirrors the
    /// plane name-table entry format (tile index/flip/palette/priority); word 3 = X (10 bits,
    /// offset by 128).</summary>
    private readonly record struct SpriteEntry(
        int Y, int Link, int HeightCells, int WidthCells, int TileIndex,
        bool FlipH, bool FlipV, int PaletteLine, bool Priority, int X);

    private ushort ReadVramWord(uint address) =>
        (ushort)((Vram[address & (VramSize - 1)] << 8) | Vram[(address + 1) & (VramSize - 1)]);

    private SpriteEntry ReadSpriteEntry(int index)
    {
        uint baseAddress = SpriteTableBase + (uint)(index * 8);
        ushort word0 = ReadVramWord(baseAddress);
        ushort word1 = ReadVramWord(baseAddress + 2);
        ushort word2 = ReadVramWord(baseAddress + 4);
        ushort word3 = ReadVramWord(baseAddress + 6);

        return new SpriteEntry(
            Y: (word0 & 0x03FF) - 128,
            Link: word1 & 0x7F,
            HeightCells: ((word1 >> 8) & 0x3) + 1,
            WidthCells: ((word1 >> 10) & 0x3) + 1,
            TileIndex: word2 & 0x07FF,
            FlipH: (word2 & 0x0800) != 0,
            FlipV: (word2 & 0x1000) != 0,
            PaletteLine: (word2 >> 13) & 0x03,
            Priority: (word2 & 0x8000) != 0,
            X: (word3 & 0x03FF) - 128);
    }

    /// <summary>Evaluates every sprite touching this scanline, in display-list order
    /// (starting from sprite 0, following each entry's link field — not attribute-table
    /// order), and returns the composited per-pixel result. Earlier sprites in the list
    /// occlude later ones; two opaque sprite pixels landing on the same spot sets the
    /// collision flag. Stops early (setting the overflow flag) past the H40 per-line sprite
    /// or pixel budget, matching real hardware's display list cutoff.
    ///
    /// Sprite masking: a sprite whose raw X field is 0 (actual on-screen X of -128, normally
    /// invisible) is repurposed by real hardware as a "stop here" signal — but only once at
    /// least one other sprite has already been drawn on this scanline. If it's the *first*
    /// visible sprite on the line, it has no special effect and is simply skipped (it's off
    /// the left edge, same as any other sprite parked there). This is a real, documented VDP
    /// quirk — some games and demos deliberately exploit it to truncate the sprite list
    /// without touching Y positions or tile data.</summary>
    private (int ColorIndex, int PaletteLine, bool Priority)?[] EvaluateSpriteLine(int scanline)
    {
        var line = new (int ColorIndex, int PaletteLine, bool Priority)?[ScreenWidth];
        int spriteCount = 0;
        int pixelBudget = 0;
        int index = 0;

        for (int i = 0; i < MaxSpritesInTable; i++)
        {
            SpriteEntry sprite = ReadSpriteEntry(index);
            int height = sprite.HeightCells * 8;

            if (scanline >= sprite.Y && scanline < sprite.Y + height)
            {
                if (sprite.X == -128)
                {
                    if (spriteCount > 0)
                    {
                        break;
                    }

                    // First visible sprite on this line and it's a mask sprite: just skip
                    // it — no masking effect, and it doesn't count against either budget.
                }
                else
                {
                    spriteCount++;
                    if (spriteCount > MaxSpritesPerLine)
                    {
                        _spriteOverflowPending = true;
                        break;
                    }

                    pixelBudget += sprite.WidthCells * 8;
                    if (pixelBudget > MaxSpritePixelsPerLine)
                    {
                        _spriteOverflowPending = true;
                        break;
                    }

                    DrawSpriteRow(sprite, scanline, line);
                }
            }

            if (sprite.Link == 0)
            {
                break;
            }

            index = sprite.Link;
        }

        return line;
    }

    /// <summary>Sub-tiles within a multi-cell sprite are stored column-major (all rows of
    /// column 0, then all rows of column 1, ...) — unlike plane name tables, which are
    /// row-major. Flipping mirrors across the sprite's full width/height, not per-tile, so
    /// flipped multi-cell sprites still reorder their sub-tiles correctly.</summary>
    private void DrawSpriteRow(SpriteEntry sprite, int scanline, (int ColorIndex, int PaletteLine, bool Priority)?[] line)
    {
        int width = sprite.WidthCells * 8;
        int height = sprite.HeightCells * 8;
        int rowInSprite = scanline - sprite.Y;

        for (int col = 0; col < width; col++)
        {
            int screenX = sprite.X + col;
            if ((uint)screenX >= ScreenWidth)
            {
                continue;
            }

            int spriteCol = sprite.FlipH ? width - 1 - col : col;
            int spriteRow = sprite.FlipV ? height - 1 - rowInSprite : rowInSprite;

            int tileCol = spriteCol >> 3;
            int tileRow = spriteRow >> 3;
            int pixelX = spriteCol & 7;
            int pixelY = spriteRow & 7;

            int tileIndex = sprite.TileIndex + tileCol * sprite.HeightCells + tileRow;
            tileIndex = ApplyInterlaceTileIndex(tileIndex);
            uint tileDataAddress = (uint)(tileIndex * 32 + pixelY * 4 + pixelX / 2);
            byte tileByte = Vram[tileDataAddress & (VramSize - 1)];
            int colorIndex = (pixelX % 2 == 0) ? (tileByte >> 4) : (tileByte & 0x0F);

            if (colorIndex == 0)
            {
                continue;
            }

            if (line[screenX].HasValue)
            {
                _spriteCollisionPending = true;
                continue;
            }

            line[screenX] = (colorIndex, sprite.PaletteLine, sprite.Priority);
        }
    }
}
