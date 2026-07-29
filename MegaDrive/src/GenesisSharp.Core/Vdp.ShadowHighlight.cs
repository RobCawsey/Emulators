namespace GenesisSharp.Core;

/// <summary>Shadow/Highlight mode (Mode 5 only). Same confidence caveat as the rest of the
/// VDP: the enable bit and operator-color positions are recalled with reasonable confidence
/// (they're fairly widely cited — Sonic 3's lighting effects are the textbook example), but
/// the exact brightness math real hardware uses (a DAC-level detail) is not something I have
/// verified. What's implemented is a plausible, clearly-differentiated approximation: shadow
/// halves each RGB channel, highlight pushes it partway to white.
///
/// The rule: once enabled, every visible pixel defaults to "shadow" brightness unless the
/// layer that actually wins compositing has its priority bit set, in which case it's normal
/// brightness. A sprite pixel using palette line 3 with color index 14 (highlight) or 15
/// (shadow) is a special *operator* — it's never drawn as a visible color itself (whatever's
/// beneath it still wins compositing normally), but it forces that underlying pixel's
/// brightness to highlight or shadow regardless of the priority-based default.</summary>
public sealed partial class Vdp
{
    public bool ShadowHighlightEnabled => (Registers[12] & 0x08) != 0;

    private static byte Darken(byte value) => (byte)(value / 2);

    private static byte Brighten(byte value) => (byte)(value + (255 - value) / 2);

    private static (byte R, byte G, byte B) ApplyShadow(byte r, byte g, byte b) => (Darken(r), Darken(g), Darken(b));

    private static (byte R, byte G, byte B) ApplyHighlight(byte r, byte g, byte b) => (Brighten(r), Brighten(g), Brighten(b));
}
