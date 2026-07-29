using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using NesSharp.Core;

namespace NesSharp.Frontend;

/// <summary>Converts a <see cref="Ppu2C02.Frame"/> palette-index buffer into a GDI+ Bitmap
/// via <see cref="NesPalette"/>. Shared between the live window and the headless screenshot
/// mode so both draw exactly the same way.</summary>
public sealed class FrameBitmapRenderer
{
    private readonly byte[] _argb = new byte[256 * 240 * 4];

    public void Render(byte[] paletteIndices, Bitmap target)
    {
        for (int i = 0; i < paletteIndices.Length; i++)
        {
            (byte r, byte g, byte b) = NesPalette.Colors[paletteIndices[i]];
            int o = i * 4;
            _argb[o + 0] = b;
            _argb[o + 1] = g;
            _argb[o + 2] = r;
            _argb[o + 3] = 255;
        }

        var rect = new Rectangle(0, 0, 256, 240);
        BitmapData data = target.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(_argb, 0, data.Scan0, _argb.Length);
        target.UnlockBits(data);
    }
}
