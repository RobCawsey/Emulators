namespace GenesisSharp.Core;

/// <summary>Interlace modes (Mode 5 only — Mode 4 has no equivalent). This is the
/// lowest-confidence corner of the whole VDP implementation: IM2 in particular sees
/// essentially no real-world use (Sonic 2's 2-player split screen is the most commonly cited
/// example), and unlike every other VDP subsystem this codebase has cross-checked against
/// genesis-plus-gx, there is no real ROM and no known-correct reference output to verify this
/// against — the algorithm below is a best-effort reconstruction from genesis-plus-gx's address
/// arithmetic (`GET_LSB_TILE_IM2`/`GET_MSB_TILE_IM2`, `vdp_render.c:148-153`), not an
/// independently confirmed fact. Treat it accordingly.
///
/// IM1 (register 12 bits 1-2 = 01) is, as far as this emulator is concerned, a pure display-
/// timing curiosity: real hardware alternates which physical scanlines each field paints on a
/// CRT, but the underlying pattern/name-table data is identical either way, so a fixed-size
/// RGB frame buffer has nothing distinguishable to render differently. It's tracked (so
/// register reads reflect it) but doesn't change any pixel output.
///
/// IM2 (bits = 11) genuinely changes addressing. Real hardware combines two fields into one
/// double-height (448-line) image; genesis-plus-gx addresses that by computing a doubled
/// vertical line index per field (<c>v_line = screenLine*2 + fieldParity</c>) and reading tile
/// data through that. This emulator renders one field per frame at the normal 224-line
/// resolution rather than merging fields into a taller buffer — the same simplification most
/// non-cycle-exact Genesis emulators make — so <c>v_line</c> isn't a quantity this emulator
/// naturally has. Working through genesis-plus-gx's tile-cache addressing by hand to translate
/// it into this emulator's per-field, 8-row-visual-slot model produces the following (see
/// <see cref="ApplyInterlaceTileAddress"/>):
///
/// - The pattern name's top bit (bit 10 of the 11-bit tile index) is dropped from tile
///   selection — genesis-plus-gx masks the index to 10 bits (0x3FF) before using it. The
///   previous version of this file doubled the *full, unmasked* 11-bit index, which for any
///   tile index &gt;= 1024 produced an out-of-range VRAM address (silently wrapped/corrupted by
///   the existing bounds mask elsewhere) — a genuine bug, now fixed regardless of confidence in
///   anything else below.
/// - The masked 10-bit index selects a *pair* of adjacent real VRAM tiles (2N and 2N+1), which
///   together form one logical 16-row-tall tile: 2N is the top half (visual rows 0-3 of this
///   field's own 8-row slot), 2N+1 is the bottom half (visual rows 4-7).
/// - Within whichever half a given row falls in, the field doesn't read that physical tile's
///   rows in order — it takes every *other* row, offset by field parity: row 0/1 of the
///   half-tile group maps to physical tile row <c>0 or 1</c>, group row 2/3 maps to physical
///   row <c>2 or 3</c>, and so on (i.e. physical row = <c>(rowWithinHalf * 2) + parity</c>).
///   This is what actually produces interlace's half-vertical-resolution-per-field effect once
///   worked through concretely, rather than the simpler (and, on inspection, incorrect) "field
///   picks tile A or tile B entirely" model the previous version implemented.
///
/// This composes with horizontal/vertical tile flip the same way it does everywhere else in
/// this codebase: flip is resolved first (producing the usual 0-7 "final" pixel row), and *that*
/// flipped value is what feeds the IM2 split above — not the raw, pre-flip row.</summary>
public sealed partial class Vdp
{
    /// <summary>0 = no interlace, 1 = IM1 (display-timing only, no addressing change),
    /// 2 = reserved/treated as no interlace, 3 = IM2 (doubled tile addressing).</summary>
    public int InterlaceMode => (Registers[12] >> 1) & 0x03;

    public bool IsInterlaceMode2 => InterlaceMode == 3;

    /// <summary>Which field is currently being drawn — toggles once per frame (see <see
    /// cref="AdvanceScanline"/>). Only meaningful while <see cref="IsInterlaceMode2"/>.</summary>
    public bool CurrentFieldIsOdd { get; private set; }

    /// <summary>Translates a raw (already flip-resolved) tile index and within-tile row into
    /// the effective values to address VRAM with. A no-op outside IM2. See the type-level
    /// remarks above for the derivation and its confidence caveat.</summary>
    private (int TileIndex, int Row) ApplyInterlaceTileAddress(int tileIndex, int row)
    {
        if (!IsInterlaceMode2)
        {
            return (tileIndex, row);
        }

        int pairIndex = tileIndex & 0x03FF;
        int half = row >> 2; // 0 = top physical tile (visual rows 0-3), 1 = bottom (rows 4-7)
        int rowWithinHalf = row & 3;
        int physicalRow = rowWithinHalf * 2 + (CurrentFieldIsOdd ? 1 : 0);

        return (pairIndex * 2 + half, physicalRow);
    }
}
