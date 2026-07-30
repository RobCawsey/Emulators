namespace GenesisSharp.Core;

/// <summary>Interlace modes (Mode 5 only — Mode 4 has no equivalent). This is the
/// lowest-confidence corner of the whole VDP implementation: IM2 in particular sees
/// essentially no real-world use (Sonic 2's 2-player split screen is the most commonly cited
/// example) and I have far less exposure to its documented behavior than anything else here.
///
/// IM1 (register 12 bits 1-2 = 01) is, as far as this emulator is concerned, a pure display-
/// timing curiosity: real hardware alternates which physical scanlines each field paints on a
/// CRT, but the underlying pattern/name-table data is identical either way, so a fixed-size
/// RGB frame buffer has nothing distinguishable to render differently. It's tracked (so
/// register reads reflect it) but doesn't change any pixel output.
///
/// IM2 (bits = 11) genuinely changes addressing: each field reads different pattern data for
/// the same name-table/sprite entry, doubling the vertical detail once both fields are
/// combined by a real interlaced display. This emulator renders one field per frame at the
/// normal 224-line resolution rather than merging fields into a taller buffer — showing
/// field-alternating content rather than a true 448-line de-interlaced image, which is the
/// same simplification most non-cycle-exact Genesis emulators make. The specific mechanism
/// implemented here — the name table's tile index selects a *pair* of tiles, doubled and
/// offset by field parity (tileIndex*2, or *2+1 on the odd field) — is a best-effort
/// reconstruction of how such doubling schemes are typically wired, not a confirmed fact.</summary>
public sealed partial class Vdp
{
    /// <summary>0 = no interlace, 1 = IM1 (display-timing only, no addressing change),
    /// 2 = reserved/treated as no interlace, 3 = IM2 (doubled tile addressing).</summary>
    public int InterlaceMode => (Registers[12] >> 1) & 0x03;

    public bool IsInterlaceMode2 => InterlaceMode == 3;

    /// <summary>Which field is currently being drawn — toggles once per frame (see <see
    /// cref="AdvanceScanline"/>). Only meaningful while <see cref="IsInterlaceMode2"/>.</summary>
    public bool CurrentFieldIsOdd { get; private set; }

    private int ApplyInterlaceTileIndex(int tileIndex) =>
        IsInterlaceMode2 ? tileIndex * 2 + (CurrentFieldIsOdd ? 1 : 0) : tileIndex;
}
