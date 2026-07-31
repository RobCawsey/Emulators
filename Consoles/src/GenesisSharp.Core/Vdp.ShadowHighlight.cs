namespace GenesisSharp.Core;

/// <summary>Shadow/Highlight mode (Mode 5 only). Confirmed against genesis-plus-gx's
/// <c>vdp_render.c</c>: the real VDP DAC is a discrete 3-bit-per-channel model, not a continuous
/// blend. <c>palette_init()</c> (`vdp_render.c:990-1001`) builds three fixed 4-bit-per-channel
/// output levels from each CRAM entry's raw 3-bit component (0-7): shadow uses the raw component
/// as-is (0-7), normal doubles it (0, 2, ..., 14), and highlight adds 7 (7-14) — i.e. shadow and
/// highlight are literally the same 3-bit value read into the bottom or top half of the DAC's
/// 4-bit range, with "normal" sitting exactly between them. <see cref="ScaleDacLevel"/> below is
/// derived from that, and happens to be numerically identical to this file's previous
/// halve/push-toward-white approximation for every possible input (both reduce to the same exact
/// rational scaling once you account for <see cref="Vdp.DecodeColor"/>'s existing 3-bit
/// normal-color quantization) — so this rewrite changes no rendered pixel, it just replaces an
/// unexplained heuristic with the real, cited formula.
///
/// The compositing rule: once enabled, every visible pixel defaults to "shadow" brightness unless
/// the layer that actually wins compositing has its priority bit set, in which case it's normal
/// brightness. Two real sprite-color-index quirks (confirmed against
/// <c>make_lut_bgobj_ste</c>, `vdp_render.c:797-921`, cross-checked against how its output byte
/// is consumed as a direct index into the shadow/normal/highlight <c>pixel[]</c> lookup at
/// `vdp_render.c:1135-1137,4977`) sit on top of that default:
/// - A sprite pixel using palette line 3 with color index 15 is a special *operator* — never
///   drawn as a visible color itself (whatever's beneath it still wins compositing normally), but
///   it unconditionally forces that underlying pixel to shadow brightness.
/// - A sprite pixel using color index 14 in palette lines 0-2 (not line 3) is drawn normally as
///   its own color, but is unconditionally forced to *normal* brightness regardless of the usual
///   priority-based default — i.e. it's immune to the automatic shadow rule.
///
/// Color index 14 on palette line 3 is left as this codebase's original "always highlight"
/// simplification (matching the widely-cited community convention, e.g. Charles MacDonald's
/// genvdp.txt) rather than changed on the strength of this investigation alone: genesis-plus-gx's
/// own logic for that specific case (`(bx &amp; 0x80) ? bf|0x80 : bf|0x40`) branches on the
/// *incoming* background pixel's own already-resolved intensity state, which is produced by an
/// earlier compositing stage (`make_lut_bg`) this investigation didn't trace fully — both literal
/// readings of that branch actually suggest the common/untraced case resolves to *normal*, not
/// highlight, which would contradict long-standing community documentation. Given that
/// contradiction and the unverified provenance of the incoming state, changing this specific case
/// isn't done here — flagged for whoever traces `make_lut_bg` next.</summary>
public sealed partial class Vdp
{
    public bool ShadowHighlightEnabled => (Registers[12] & 0x08) != 0;

    /// <summary>Extracts the raw 3-bit R/G/B components (0-7) from a CRAM word, matching
    /// <see cref="DecodeColor"/>'s bit layout (bits 1-3 = R, 5-7 = G, 9-11 = B).</summary>
    private static (int R, int G, int B) DecodeRawComponents(ushort cramValue) =>
        ((cramValue >> 1) & 0x7, (cramValue >> 5) & 0x7, (cramValue >> 9) & 0x7);

    /// <summary>Scales a 4-bit DAC output level (0-14 — the real VDP DAC's per-channel range,
    /// though shadow/normal/highlight only ever individually produce 0-7, even 0-14, or 7-14
    /// respectively) to an 8-bit display value. Chosen so it exactly agrees with
    /// <see cref="DecodeColor"/>'s existing <c>component*255/7</c> normal-color scaling at every
    /// possible 3-bit input — <c>(2k)*255/14</c> and <c>k*255/7</c> are the same rational number
    /// for every integer k, so normal-brightness output is unchanged by this file.</summary>
    private static byte ScaleDacLevel(int level) => (byte)(level * 255 / 14);

    private static (byte R, byte G, byte B) ApplyShadow(ushort cramValue)
    {
        var (r, g, b) = DecodeRawComponents(cramValue);
        return (ScaleDacLevel(r), ScaleDacLevel(g), ScaleDacLevel(b));
    }

    private static (byte R, byte G, byte B) ApplyHighlight(ushort cramValue)
    {
        var (r, g, b) = DecodeRawComponents(cramValue);
        return (ScaleDacLevel(r + 7), ScaleDacLevel(g + 7), ScaleDacLevel(b + 7));
    }
}
