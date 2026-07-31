namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    /// <summary>RGB24, row-major, <see cref="ScreenWidth"/>x<see cref="ScreenHeight"/>.</summary>
    public byte[] FrameBuffer { get; } = new byte[ScreenWidth * ScreenHeight * 3];

    /// <summary>Renders every scanline. Plane A, plane B, the window, and sprites are all
    /// composited, with all three horizontal scroll modes and both vertical scroll modes (see
    /// the type-level remarks on confidence for the bit layouts this depends on).</summary>
    public void RenderFrame()
    {
        for (int line = 0; line < ScreenHeight; line++)
        {
            RenderScanline(line);
        }
    }

    public void RenderScanline(int scanline)
    {
        if ((uint)scanline >= ScreenHeight)
        {
            return;
        }

        if (!DisplayEnabled)
        {
            int blankOffset = scanline * ScreenWidth * 3;
            Array.Clear(FrameBuffer, blankOffset, ScreenWidth * 3);
            return;
        }

        if (Mode4Enabled)
        {
            RenderMode4Scanline(scanline);
            return;
        }

        int hScrollA = -ReadHScrollValue(0, scanline);
        int hScrollB = -ReadHScrollValue(1, scanline);

        int planeWidth = PlaneWidthTiles;
        int planeHeight = PlaneHeightTiles;
        var spriteLine = EvaluateSpriteLine(scanline);
        bool windowActiveThisRow = IsWindowActiveVertically(scanline);
        int activeWidth = ActiveWidth;
        bool shEnabled = ShadowHighlightEnabled;

        for (int x = 0; x < activeWidth; x++)
        {
            int vScrollA = ReadVScrollValue(0, x);
            int vScrollB = ReadVScrollValue(1, x);

            // The window plane replaces plane A's slot wherever it's active — plane B and
            // sprites composite against it exactly the same way, using its own priority bit.
            var a = windowActiveThisRow || IsWindowActiveHorizontally(x)
                ? GetWindowPixel(x, scanline)
                : GetPlanePixel(PlaneANameTableBase, planeWidth, planeHeight, x, scanline, hScrollA, vScrollA);
            var b = GetPlanePixel(PlaneBNameTableBase, planeWidth, planeHeight, x, scanline, hScrollB, vScrollB);
            var s = spriteLine[x];

            // Under shadow/highlight, a sprite pixel using palette line 3 with color index 15 is
            // a special operator, not a real color: it never wins compositing itself (whatever's
            // beneath it shows through normally) but unconditionally forces that pixel to shadow
            // brightness (confirmed against genesis-plus-gx's make_lut_bgobj_ste — see
            // Vdp.ShadowHighlight.cs's type-level remarks for the full citation and for why color
            // index 14 on palette line 3 is deliberately left as this codebase's original
            // "always highlight" simplification rather than changed here).
            bool isHighlightOperator = shEnabled && s.HasValue && s.Value.PaletteLine == 3 && s.Value.ColorIndex == 14;
            bool isShadowOperator = shEnabled && s.HasValue && s.Value.PaletteLine == 3 && s.Value.ColorIndex == 15;
            var effectiveSprite = isHighlightOperator || isShadowOperator ? null : s;

            // Priority order, highest to lowest: high-priority sprite, high-priority A,
            // high-priority B, low-priority sprite, low-priority A, low-priority B, backdrop.
            // The third element tracks whether the winning layer was high-priority, and the
            // fourth whether it was a sprite — both feed the brightness decision below.
            (int ColorIndex, int PaletteLine, bool HighPriority, bool IsSprite)? chosen =
                effectiveSprite.HasValue && effectiveSprite.Value.Priority ? (effectiveSprite.Value.ColorIndex, effectiveSprite.Value.PaletteLine, true, true) :
                a.Priority && a.ColorIndex != 0 ? (a.ColorIndex, a.PaletteLine, true, false) :
                b.Priority && b.ColorIndex != 0 ? (b.ColorIndex, b.PaletteLine, true, false) :
                effectiveSprite.HasValue ? (effectiveSprite.Value.ColorIndex, effectiveSprite.Value.PaletteLine, false, true) :
                a.ColorIndex != 0 ? (a.ColorIndex, a.PaletteLine, false, false) :
                b.ColorIndex != 0 ? (b.ColorIndex, b.PaletteLine, false, false) :
                null;

            // A sprite pixel using color index 14 on palette lines 0-2 (not line 3, which is the
            // operator case above) is drawn as its own color but is unconditionally immune to the
            // default shadow rule — confirmed against make_lut_bgobj_ste's unconditional
            // "sf|0x40" (normal-brightness bucket) branch for sf in {0x0E, 0x1E, 0x2E}.
            bool isForcedNormalSprite = shEnabled && chosen.HasValue && chosen.Value.IsSprite
                && chosen.Value.PaletteLine != 3 && chosen.Value.ColorIndex == 14;

            ushort cramValue = chosen.HasValue
                ? Cram[chosen.Value.PaletteLine * 16 + chosen.Value.ColorIndex]
                : Cram[BackgroundPaletteLine * 16 + BackgroundColorIndex];

            var (r, g, bl) = DecodeColor(cramValue);

            if (shEnabled)
            {
                if (isHighlightOperator) (r, g, bl) = ApplyHighlight(cramValue);
                else if (isShadowOperator) (r, g, bl) = ApplyShadow(cramValue);
                else if (isForcedNormalSprite) { /* immune to the default shadow rule below */ }
                else if (!(chosen.HasValue && chosen.Value.HighPriority)) (r, g, bl) = ApplyShadow(cramValue);
            }

            int offset = (scanline * ScreenWidth + x) * 3;
            FrameBuffer[offset] = r;
            FrameBuffer[offset + 1] = g;
            FrameBuffer[offset + 2] = bl;
        }

        // H32 only draws the first 256 of the frame buffer's 320 columns above — blank the
        // rest so a leftover H40 frame doesn't linger down the right edge after a mode switch.
        if (activeWidth < ScreenWidth)
        {
            int tailOffset = (scanline * ScreenWidth + activeWidth) * 3;
            Array.Clear(FrameBuffer, tailOffset, (ScreenWidth - activeWidth) * 3);
        }
    }

    /// <summary>Selects the H-scroll table entry for <paramref name="scanline"/> per register
    /// 11's raw low 2 bits, matching real hardware's actual addressing scheme (confirmed against
    /// genesis-plus-gx's <c>hscroll_mask_table</c>: {0x00, 0x07, 0xF8, 0xFF} indexed by those same
    /// 2 bits) rather than a densely-packed "one 4-byte entry per row index" scheme. Real hardware
    /// always uses the full per-scanline addressing formula (byte offset = scanline*4) and simply
    /// masks which bits of the scanline number vary the offset -- full-screen masks them all off
    /// (always offset 0), per-row masks off only the low 3 bits (offset jumps by 32 bytes every 8
    /// lines, NOT by 4 bytes per row index), and per-scanline doesn't mask anything. Previously
    /// implemented as `index = scanline>>3; address = base + index*4`, which packed per-row entries
    /// 4 bytes apart instead of 32 -- found via the Omega Blast title-screen h-scroll shear
    /// investigation, where a real per-frame update routine's writes (to base+0/32/64/96) were
    /// being read back from entirely the wrong table offsets under the old formula, while the
    /// *actual* offsets it wasn't updating still held stale leftover VRAM content.</summary>
    private int ReadHScrollValue(int plane, int scanline)
    {
        int mask = (Registers[11] & 0x03) switch
        {
            0 => 0x00,
            1 => 0x07,
            2 => 0xF8,
            3 => 0xFF,
            _ => 0x00,
        };

        uint address = HScrollTableBase + (uint)((scanline & mask) << 2) + (uint)(plane * 2);
        return unchecked((short)ReadVramWord(address));
    }

    /// <summary>Selects the VSRAM entry for <paramref name="screenX"/> when <see
    /// cref="VerticalScrollIsPerColumn"/> is set: one shared value per plane (full-screen), or
    /// one per 2-column (16px) group — 20 groups for H40, each holding a plane A/plane B word
    /// pair, which is exactly VSRAM's 40-word size.</summary>
    private int ReadVScrollValue(int plane, int screenX)
    {
        if (!VerticalScrollIsPerColumn)
        {
            return unchecked((short)Vsram[plane]);
        }

        int index = (screenX >> 4) * 2 + plane;
        return unchecked((short)Vsram[index % VsramSize]);
    }

    private (int ColorIndex, int PaletteLine, bool Priority) GetPlanePixel(
        uint nameTableBase, int planeWidthTiles, int planeHeightTiles, int screenX, int screenY, int hScroll, int vScroll)
    {
        int worldX = (screenX + hScroll) & (planeWidthTiles * 8 - 1);
        int worldY = (screenY + vScroll) & (planeHeightTiles * 8 - 1);

        int tileX = worldX >> 3;
        int tileY = worldY >> 3;
        int pixelX = worldX & 7;
        int pixelY = worldY & 7;

        int nameTableIndex = tileY * planeWidthTiles + tileX;
        uint entryAddress = nameTableBase + (uint)(nameTableIndex * 2);
        ushort entry = (ushort)((Vram[entryAddress & (VramSize - 1)] << 8) | Vram[(entryAddress + 1) & (VramSize - 1)]);

        var (tileIndex, paletteLine, flipH, flipV, priority) = DecodeNameTableEntry(entry);

        int finalPixelX = flipH ? 7 - pixelX : pixelX;
        int finalPixelY = flipV ? 7 - pixelY : pixelY;
        (tileIndex, finalPixelY) = ApplyInterlaceTileAddress(tileIndex, finalPixelY);

        uint tileDataAddress = (uint)(tileIndex * 32 + finalPixelY * 4 + finalPixelX / 2);
        byte tileByte = Vram[tileDataAddress & (VramSize - 1)];
        int colorIndex = (finalPixelX % 2 == 0) ? (tileByte >> 4) : (tileByte & 0x0F);

        return (colorIndex, paletteLine, priority);
    }

    private static (int TileIndex, int PaletteLine, bool FlipH, bool FlipV, bool Priority) DecodeNameTableEntry(ushort entry) => (
        entry & 0x07FF,
        (entry >> 13) & 0x03,
        (entry & 0x0800) != 0,
        (entry & 0x1000) != 0,
        (entry & 0x8000) != 0
    );

    /// <summary>CRAM word -> RGB24. Best-recollection layout: 3 bits per channel at bits
    /// 1-3 (R), 5-7 (G), 9-11 (B) — the classic Genesis "even values only" 3-bit-per-channel
    /// quirk — scaled up to 0-255 for display.</summary>
    public static (byte R, byte G, byte B) DecodeColor(ushort cramValue)
    {
        int r3 = (cramValue >> 1) & 0x7;
        int g3 = (cramValue >> 5) & 0x7;
        int b3 = (cramValue >> 9) & 0x7;
        return ((byte)(r3 * 255 / 7), (byte)(g3 * 255 / 7), (byte)(b3 * 255 / 7));
    }
}
