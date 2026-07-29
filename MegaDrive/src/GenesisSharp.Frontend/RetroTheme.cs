using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace GenesisSharp.Frontend;

/// <summary>Palette, fonts, and small styling helpers for the "16-bit console menu" look applied
/// to this frontend's window chrome and dialogs -- high-contrast, evoking a Genesis-era console
/// UI rather than any one specific game. Menus, buttons, and text stay flat and hard-edged (see
/// <see cref="ApplyPixelRenderingHints"/>); <see cref="DrawGradientBorder"/> is the deliberate
/// exception, modeled directly on a reference image the user supplied of a rounded, glossy quiz-
/// panel frame -- a diagonal cyan-to-magenta gradient with rounded corners, not a flat pixel-art
/// band. Deliberately does NOT reach into the emulated screen itself (<see cref="MainForm"/>'s
/// render path is untouched) or <see cref="DebugForm"/>'s functional readout colors -- the
/// register/disassembly text boxes' own Lime/Cyan/Yellow are load-bearing (they distinguish
/// 68000 vs. Z80 vs. status text at a glance), not decoration, so they're left alone.
///
/// No pixel-art bitmap font ships with this repo (adding one would mean bundling a third-party
/// font file), so <see cref="PixelFont"/> is the closest a built-in Windows font gets to that
/// look: a bold monospace font rendered with anti-aliasing turned off, which reads as blocky and
/// "computer-y" rather than smooth.</summary>
internal static class RetroTheme
{
    // Deep blue-teal, leaning into the border's cyan end (see DrawGradientBorder) rather than
    // its magenta end -- a prior indigo/purple version leaned the other way and didn't land.
    // Panel/PanelLight keep the same relative "resting/hover" step above Background.
    public static readonly Color Background = Color.FromArgb(10, 46, 64);
    public static readonly Color Panel = Color.FromArgb(16, 70, 94);
    public static readonly Color PanelLight = Color.FromArgb(24, 96, 124);
    public static readonly Color Border = Color.FromArgb(88, 224, 232);
    public static readonly Color Accent = Color.FromArgb(248, 88, 176);
    public static readonly Color Text = Color.FromArgb(232, 232, 248);
    public static readonly Color DisabledText = Color.FromArgb(104, 100, 140);

    public static readonly Font PixelFont = new(FontFamily.GenericMonospace, 9.5f, FontStyle.Bold);
    public static readonly Font PixelFontLarge = new(FontFamily.GenericMonospace, 12f, FontStyle.Bold);

    /// <summary>Turns off anti-aliasing for both text and shapes -- soft edges are what makes
    /// GDI+'s default rendering read as "smooth modern Windows"; hard edges are most of what
    /// reads as "retro" here, independent of color choice.</summary>
    public static void ApplyPixelRenderingHints(Graphics g)
    {
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        g.SmoothingMode = SmoothingMode.None;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    /// <summary>Flat retro button look, applied in place: solid fill, single bright-cyan border,
    /// no native 3D bevel/gradient (<see cref="FlatStyle.Flat"/> suppresses that; <see
    /// cref="Button.FlatAppearance"/> below controls what replaces it).</summary>
    public static void StyleButton(ButtonBase button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = Panel;
        button.ForeColor = Text;
        button.Font = PixelFont;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.BorderSize = 2;
        button.FlatAppearance.MouseOverBackColor = PanelLight;
        button.FlatAppearance.MouseDownBackColor = Accent;
    }

    public static void StyleForm(Form form)
    {
        form.BackColor = Background;
        form.ForeColor = Text;
    }

    /// <summary>Scale factor between <paramref name="control"/>'s current monitor DPI and the
    /// 96-DPI baseline every fixed-pixel default-size constant in this frontend (<see
    /// cref="MainForm"/>'s and <see cref="DebugForm"/>'s initial <see cref="Control.ClientSize"/>,
    /// <see cref="RetroMessageBox"/>'s message wrap width, <see cref="RetroHelpDialog"/>'s
    /// default size) assumes. Multiply a design-time pixel size by this before applying it, so
    /// windows/dialogs open at a comparable APPARENT size on a high-DPI ("high definition")
    /// display instead of the same fixed physical pixel count, which reads as tiny once
    /// everything else on screen is scaled up. Requires <c>PerMonitorV2</c> DPI awareness (set
    /// via <c>&lt;ApplicationHighDpiMode&gt;</c> in the .csproj) for <see
    /// cref="Control.DeviceDpi"/> to reflect the monitor's real value rather than a stale
    /// system-wide default.</summary>
    public static float GetDpiScale(Control control) => control.DeviceDpi / 96f;

    /// <summary>Applied on top of <see cref="GetDpiScale"/> everywhere that scale is used --
    /// a straight 10% bump to every window/dialog's default size, per direct request, layered
    /// on top of (not instead of) the DPI scaling above.</summary>
    public const float DefaultSizeBoost = 1.1f;

    /// <summary>Thickness of the frame <see cref="DrawGradientBorder"/> paints, and the corner
    /// radius it rounds that frame (and, via <see cref="CreateRoundedRectanglePath"/>, the whole
    /// window's <see cref="Form.Region"/>) to -- both exposed so a custom-chrome <see
    /// cref="Form"/> (which has no native border to size around) can size its own <see
    /// cref="Form.Padding"/> to match exactly.</summary>
    public const int FrameThickness = 10;
    public const int CornerRadius = 20;

    /// <summary>Builds a rounded-rectangle outline as a fillable/regionable path -- used both to
    /// paint <see cref="DrawGradientBorder"/>'s frame and, separately, to clip an entire <see
    /// cref="Form"/> to the same rounded shape via its <see cref="Form.Region"/> (there's no
    /// "rounded window" property; clipping the whole window to this exact path is how a
    /// borderless Form gets rounded corners at all, chrome included).</summary>
    public static GraphicsPath CreateRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2) * 2;
        var corner = new Rectangle(bounds.X, bounds.Y, diameter, diameter);

        path.AddArc(corner, 180, 90);
        corner.X = bounds.Right - diameter;
        path.AddArc(corner, 270, 90);
        corner.Y = bounds.Bottom - diameter;
        path.AddArc(corner, 0, 90);
        corner.X = bounds.X;
        path.AddArc(corner, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Same idea as <see cref="CreateRoundedRectanglePath"/>, but only the top-left and
    /// top-right corners are rounded -- square at the bottom. Used to clip a title bar's own
    /// <see cref="Control.Region"/> so its top corners continue the window's curve instead of
    /// cutting a flat square notch across it (the title bar sits well clear of the window's
    /// bottom corners, so those never need rounding).</summary>
    public static GraphicsPath CreateTopRoundedRectanglePath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        if (radius <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        int diameter = Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2) * 2;
        var corner = new Rectangle(bounds.X, bounds.Y, diameter, diameter);
        int midY = bounds.Y + diameter / 2;

        path.AddArc(corner, 180, 90);
        corner.X = bounds.Right - diameter;
        path.AddArc(corner, 270, 90);
        path.AddLine(bounds.Right, midY, bounds.Right, bounds.Bottom);
        path.AddLine(bounds.Right, bounds.Bottom, bounds.X, bounds.Bottom);
        path.AddLine(bounds.X, bounds.Bottom, bounds.X, midY);
        path.CloseFigure();
        return path;
    }

    /// <summary>Fills a rounded, glossy border ring around <paramref name="bounds"/> -- a
    /// diagonal gradient from <see cref="Border"/> (cyan) at the top-left corner to <see
    /// cref="Accent"/> (magenta) at the bottom-right, modeled directly on a reference image the
    /// user supplied of a rounded quiz-panel frame with exactly that color flow, plus a thin
    /// lighter rim highlight just inside the outer edge to suggest the glossy "tube" reflection
    /// visible in that reference. Cut out in the middle with a smaller rounded rectangle of <see
    /// cref="Background"/> so only a <see cref="FrameThickness"/>-wide ring is actually visible.
    /// Needs anti-aliasing (unlike the rest of this theme -- see <see
    /// cref="ApplyPixelRenderingHints"/>) since a rounded edge drawn with it off looks like a
    /// staircase, not a curve.
    ///
    /// No-ops on a zero-size <paramref name="bounds"/> -- <see
    /// cref="System.Drawing.Drawing2D.LinearGradientBrush"/>'s constructor throws
    /// <see cref="ArgumentException"/> outright on one, rather than just drawing nothing, and a
    /// minimized window's own <see cref="Control.ClientRectangle"/> genuinely is
    /// <c>{0,0,0,0}</c> -- confirmed via a real crash when this frame's earlier gradient-based
    /// version minimized without this check.</summary>
    public static void DrawGradientBorder(Graphics g, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using GraphicsPath outerPath = CreateRoundedRectanglePath(bounds, CornerRadius);
        using var gradientBrush = new LinearGradientBrush(bounds, Border, Accent, LinearGradientMode.ForwardDiagonal);
        g.FillPath(gradientBrush, outerPath);

        // A thin, semi-transparent white outline just inside the outer edge -- a cheap
        // approximation of the specular highlight a glossy rounded tube would actually only
        // catch along part of its perimeter (top-left, facing an implied light source), but a
        // full rim reads as "glossy sheen" well enough on its own without needing a partial
        // arc-only stroke.
        using GraphicsPath highlightPath = CreateRoundedRectanglePath(Rectangle.Inflate(bounds, -2, -2), Math.Max(0, CornerRadius - 2));
        using var highlightPen = new Pen(Color.FromArgb(110, 255, 255, 255), 2);
        g.DrawPath(highlightPen, highlightPath);

        Rectangle innerBounds = Rectangle.Inflate(bounds, -FrameThickness, -FrameThickness);
        int innerRadius = Math.Max(0, CornerRadius - FrameThickness);
        using GraphicsPath innerPath = CreateRoundedRectanglePath(innerBounds, innerRadius);
        using var backgroundBrush = new SolidBrush(Background);
        g.FillPath(backgroundBrush, innerPath);
    }
}
