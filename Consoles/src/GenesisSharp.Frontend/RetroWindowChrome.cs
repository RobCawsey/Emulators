using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace GenesisSharp.Frontend;

/// <summary>Adds the same rounded, glossy-gradient custom chrome <see cref="RetroMessageBox"/>
/// uses to an existing RESIZABLE <see cref="Form"/> -- <see cref="MainForm"/> and <see
/// cref="DebugForm"/> each construct one of these as the LAST step of their own constructor (see
/// the remarks on <see cref="RetroWindowChrome(Form, bool, bool)"/> for why the ordering matters)
/// and forward two window messages to it from their own <see cref="Control.WndProc"/> override --
/// see <see cref="HandleWndProc"/>'s remarks for why that specific hook point, rather than
/// something fully self-contained, is necessary here.
///
/// Composition, not inheritance: both host forms are already `sealed class : Form`, so this is a
/// helper object a form builds and holds onto, not a base class to derive from.
///
/// <see cref="RetroMessageBox"/> does NOT use this class -- it's a fixed-size, non-resizable
/// modal, so its own simpler hand-rolled title-bar-drag (no window-message interception needed at
/// all) is a better fit than the heavier machinery here, which exists specifically to make
/// resizing and maximizing a borderless window feel native rather than reimplementing every part
/// of that by hand.</summary>
internal sealed class RetroWindowChrome
{
    private const int DefaultTitleBarHeight = 30;
    private const int ResizeBorderMargin = 8;
    private const int MinButtonWidth = 34;

    /// <summary>A plain gap below the title bar, in <see cref="RetroTheme.Background"/> --
    /// without it, the title bar and whatever's docked immediately below it (a <see
    /// cref="MenuStrip"/>, in both current hosts) are two similarly-colored bars separated only
    /// by the title bar's own 2px seam line, which reads as one squashed, merged strip rather
    /// than two distinct bars.</summary>
    private const int TitleBarSpacing = 6;

    /// <summary>The padding this chrome insets a host form by, on all four sides, to make room
    /// for the painted gradient frame (see <see cref="RetroTheme.DrawGradientBorder"/>) plus, on
    /// top, the title bar. Uniform on all four sides -- the title bar itself is a separate
    /// Dock=Top child with its own <see cref="_titleBarHeight"/>, reserved by the normal docking
    /// flow, NOT by padding; baking that height into this padding's top value too (an earlier
    /// version of this file did exactly that) double-reserves that height, pushing the title bar
    /// down by a whole extra bar's worth of empty space below the actual top border. The
    /// constructor grows the host's own <see cref="Control.ClientSize"/> by this padding PLUS
    /// <see cref="_titleBarHeight"/> separately (see the constructor), so whatever content area a
    /// host already sized itself for (the emulated screen, the VRAM tile grid, ...) ends up the
    /// size it was designed for rather than shrinking by however much room the new chrome
    /// takes.</summary>
    public static readonly Padding FramePadding = new(RetroTheme.FrameThickness + 1);

    private readonly Form _form;
    private readonly Panel _titleBar;
    private readonly Label _titleLabel;
    private readonly Label? _maximizeButton;
    private readonly bool _allowMaximize;

    /// <summary>Matched to the host's own <see cref="Form.MainMenuStrip"/> real preferred height
    /// (both current hosts set one before constructing this chrome -- see the constructor's
    /// remarks on ordering) rather than a fixed constant. A fixed height here previously left the
    /// title bar visibly squashed next to the taller menu strip immediately below it once font/
    /// DPI made the menu strip's own auto-sized height grow independently of it -- a real
    /// screenshot report. Falls back to <see cref="DefaultTitleBarHeight"/>, DPI-scaled, for any
    /// future host with no <see cref="Form.MainMenuStrip"/> at all.</summary>
    private readonly int _titleBarHeight;

    /// <summary>Shared width for all three title-bar buttons, wide enough for the widest glyph
    /// among them ("[ ]"/"[=]") plus a little breathing room -- computed once so minimize/
    /// maximize/close all end up the same width, rather than each guessing independently.
    /// <see cref="MinButtonWidth"/> alone was too narrow for "[ ]" at some fonts/DPI settings:
    /// with <c>AutoSize</c> off (see <see cref="CreateTitleBarButton"/>'s remarks), a <see
    /// cref="Label"/> that doesn't fit its text WRAPS it onto a second line instead of clipping
    /// it, which is what made the maximize glyph render small and out of vertical alignment with
    /// its single-line siblings in a real screenshot report.</summary>
    private readonly int _buttonWidth;

    /// <summary>A maximized window fills the screen -- there's no "outside" for a border frame
    /// to read against, so maximized state drops the frame entirely (<see
    /// cref="UpdateChromeLayout"/>) rather than drawing it uselessly. Only the title bar's own
    /// height survives as padding.</summary>
    private readonly Padding _maximizedPadding;

    private bool _dragging;
    private bool _dragStartWasMaximized;
    private Point _dragStartMouseScreen;
    private Point _dragStartFormLocation;

    /// <summary>Must run after the host form has already added whatever it wants docked to the
    /// top (typically a <see cref="MenuStrip"/> or a toolbar) -- controls docked to the same edge
    /// are docked in REVERSE of their add order (the last one added ends up CLOSEST to that
    /// edge; see <see cref="RetroMessageBox"/>'s remarks for the same rule, confirmed there via a
    /// real layout bug), so the title bar this constructor adds needs to be added after
    /// everything else already sharing the top edge, or it lands below that content instead of
    /// above it.</summary>
    public RetroWindowChrome(Form form, bool allowMinimize = true, bool allowMaximize = true)
    {
        _form = form;
        _allowMaximize = allowMaximize;

        // Measured before anything else here touches sizing -- both current hosts set
        // MainMenuStrip right after adding their own MenuStrip, well before constructing this
        // chrome as the LAST step of their constructor (see this constructor's own remarks), so
        // it's already available to measure against.
        int menuStripHeight = form.MainMenuStrip?.GetPreferredSize(Size.Empty).Height ?? 0;
        _titleBarHeight = menuStripHeight > 0 ? menuStripHeight : (int)(DefaultTitleBarHeight * RetroTheme.GetDpiScale(form));
        _maximizedPadding = new Padding(0, _titleBarHeight, 0, 0);

        // Measured against "[ ]" specifically -- the widest of the three possible button glyphs
        // ("[=]" measures the same three characters) -- so it's never a near miss that only
        // wraps at some fonts/DPI settings but not others.
        int widestGlyphWidth = TextRenderer.MeasureText("[ ]", RetroTheme.PixelFont).Width;
        _buttonWidth = Math.Max(MinButtonWidth, widestGlyphWidth + 16);

        RetroTheme.StyleForm(form);
        form.FormBorderStyle = FormBorderStyle.None;
        form.MinimumSize = new Size(FramePadding.Horizontal + 160, FramePadding.Vertical + _titleBarHeight + TitleBarSpacing + 100);
        form.Padding = FramePadding;
        // Height grows by the title bar's own height (plus its spacing gap) too, on top of the
        // border padding -- both are genuinely additional space beyond what the host already
        // sized itself for, not something the border padding alone accounts for (see
        // FramePadding's remarks).
        form.ClientSize = new Size(form.ClientSize.Width + FramePadding.Horizontal, form.ClientSize.Height + FramePadding.Vertical + _titleBarHeight + TitleBarSpacing);

        // A shade lighter than the menu strip's own Panel color -- distinct enough to read as
        // its own strip while still clearly part of the same color family.
        _titleBar = new Panel { Dock = DockStyle.Top, Height = _titleBarHeight, BackColor = RetroTheme.PanelLight };
        _titleBar.Paint += (_, e) =>
        {
            // A single darker line along the bottom edge is enough to read as a seam between
            // the title bar and the body below, without needing a second child control -- still
            // needed even now that the title bar and menu strip share the same Panel color,
            // since otherwise the two would blend into one undifferentiated band.
            using var seam = new Pen(RetroTheme.Background, 2);
            e.Graphics.DrawLine(seam, 0, _titleBar.Height - 1, _titleBar.Width, _titleBar.Height - 1);
        };

        _titleLabel = new Label
        {
            Text = form.Text,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };
        form.TextChanged += (_, _) => _titleLabel.Text = form.Text;

        var closeButton = CreateTitleBarButton("X");
        closeButton.Click += (_, _) => form.Close();
        closeButton.MouseEnter += (_, _) => closeButton.BackColor = Color.FromArgb(200, 32, 64);
        closeButton.MouseLeave += (_, _) => closeButton.BackColor = _titleBar.BackColor;

        Label? maximizeButton = null;
        if (allowMaximize)
        {
            maximizeButton = CreateTitleBarButton("[ ]");
            maximizeButton.Click += (_, _) => ToggleMaximize();
        }

        _maximizeButton = maximizeButton;

        Label? minimizeButton = null;
        if (allowMinimize)
        {
            minimizeButton = CreateTitleBarButton("_");
            minimizeButton.Click += (_, _) => form.WindowState = FormWindowState.Minimized;
        }

        // Same reverse-add-order rule as above, applied within the title bar itself: close must
        // be added LAST to land at the far right (closest to that edge), so left-to-right this
        // reads as [minimize][maximize][close] -- the conventional order.
        if (minimizeButton is not null)
        {
            _titleBar.Controls.Add(minimizeButton);
        }

        if (maximizeButton is not null)
        {
            _titleBar.Controls.Add(maximizeButton);
        }

        _titleBar.Controls.Add(closeButton);
        _titleBar.Controls.Add(_titleLabel);

        // Dragging and double-click-to-maximize are ordinary client-area mouse events on the
        // title bar's own controls -- WinForms delivers these correctly without any message-
        // level interception, unlike the resize borders (see HandleWndProc's remarks).
        _titleBar.MouseDown += TitleBar_MouseDown;
        _titleBar.MouseMove += TitleBar_MouseMove;
        _titleBar.MouseUp += TitleBar_MouseUp;
        _titleBar.DoubleClick += (_, _) => ToggleMaximize();
        _titleLabel.MouseDown += TitleBar_MouseDown;
        _titleLabel.MouseMove += TitleBar_MouseMove;
        _titleLabel.MouseUp += TitleBar_MouseUp;
        _titleLabel.DoubleClick += (_, _) => ToggleMaximize();

        // Added after everything the host already put on the top edge but before _titleBar --
        // same reverse-add-order rule as elsewhere in this file, so this spacer lands between
        // the title bar (added last, closest to the top edge) and whatever the host docked to
        // Top (e.g. a MenuStrip), not above the title bar or below the host's own content.
        var titleBarSpacer = new Panel { Dock = DockStyle.Top, Height = TitleBarSpacing, BackColor = RetroTheme.Background };
        form.Controls.Add(titleBarSpacer);
        form.Controls.Add(_titleBar);

        form.Resize += (_, _) => UpdateChromeLayout();
        UpdateChromeLayout();
    }

    private Label CreateTitleBarButton(string text)
    {
        var button = new Label
        {
            Text = text,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
            Dock = DockStyle.Right,
            // Label.AutoSize defaults to true, which fights Dock=Right + the explicit Width
            // below: each button's own glyph ("_", "[ ]", "X") has different ink dimensions, so
            // without this each button was autosizing to a slightly different box instead of
            // uniformly filling the title bar -- a real reported misalignment between the three.
            AutoSize = false,
            Width = _buttonWidth,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        return button;
    }

    private void ToggleMaximize()
    {
        if (!_allowMaximize)
        {
            return;
        }

        _form.WindowState = _form.WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    /// <summary>Re-derives everything that depends on <see cref="Form.WindowState"/>: whether
    /// the rounded/gradient frame still makes sense at all (dropped entirely while maximized --
    /// see <see cref="_maximizedPadding"/>'s remarks), the maximize button's own glyph (a plain
    /// bracket swap standing in for a real restore-icon, consistent with this theme not bundling
    /// icon assets -- see <see cref="RetroTheme"/>'s remarks on <see cref="RetroTheme.PixelFont"/>
    /// for the same reasoning applied to text), and the whole window's <see cref="Form.Region"/>
    /// -- there's no "rounded window" property, so clipping the whole window to a rounded-
    /// rectangle Region is how a borderless Form gets rounded corners at all, chrome
    /// included.</summary>
    private void UpdateChromeLayout()
    {
        bool maximized = _form.WindowState == FormWindowState.Maximized;
        _form.Padding = maximized ? _maximizedPadding : FramePadding;

        if (_maximizeButton is not null)
        {
            _maximizeButton.Text = maximized ? "[=]" : "[ ]";
        }

        if (maximized || _form.ClientSize.Width <= 0 || _form.ClientSize.Height <= 0)
        {
            _form.Region = null;
        }
        else
        {
            using GraphicsPath path = RetroTheme.CreateRoundedRectanglePath(new Rectangle(Point.Empty, _form.ClientSize), RetroTheme.CornerRadius);
            _form.Region = new Region(path);
        }

        // The title bar's own top corners need the same rounding, in the same maximized-drops-
        // it/normal-has-it state as the window itself -- otherwise its flat rectangular corners
        // cut a square notch across the window's rounded curve instead of continuing it (a real
        // "doesn't blend" bug this fixes). Uses the border's INNER radius (the curve bounding the
        // content area, not the window's outer edge), since that's the curve the title bar
        // actually sits against.
        if (maximized || _titleBar.Width <= 0 || _titleBar.Height <= 0)
        {
            _titleBar.Region = null;
        }
        else
        {
            int innerRadius = Math.Max(0, RetroTheme.CornerRadius - RetroTheme.FrameThickness);
            using GraphicsPath titleBarPath = RetroTheme.CreateTopRoundedRectanglePath(new Rectangle(Point.Empty, _titleBar.Size), innerRadius);
            _titleBar.Region = new Region(titleBarPath);
        }

        _form.Invalidate();
    }

    /// <summary>Paints the frame -- call this from the host form's own <c>Paint</c> handler
    /// rather than having this class wire itself up internally, so a host with its own painting
    /// needs (<see cref="MainForm"/>'s display panel, <see cref="DebugForm"/>'s VRAM viewer) stays
    /// in full control of its own paint wiring. Only actually painted in <see
    /// cref="FormWindowState.Normal"/> -- not just "not maximized," which was a real bug: a
    /// minimized window's <see cref="Control.ClientRectangle"/> is <c>{0,0,0,0}</c>, and a
    /// zero-size rectangle crashes <see cref="System.Drawing.Drawing2D.LinearGradientBrush"/>'s
    /// constructor outright. Maximized is skipped too, matching <see
    /// cref="UpdateChromeLayout"/>'s Region/Padding handling.</summary>
    public void PaintFrame(Graphics g)
    {
        if (_form.WindowState == FormWindowState.Normal)
        {
            RetroTheme.DrawGradientBorder(g, _form.ClientRectangle);
        }
    }

    /// <summary>Tracked in screen coordinates rather than sender-relative ones deliberately --
    /// the mouse events fire from either the title bar panel or its child label, each with its
    /// own coordinate origin, so screen-space avoids having to know which one raised this (same
    /// reasoning as <see cref="RetroMessageBox"/>'s identical drag handling).</summary>
    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _dragStartMouseScreen = Cursor.Position;
        _dragStartFormLocation = _form.Location;
        _dragStartWasMaximized = _form.WindowState == FormWindowState.Maximized;
    }

    private void TitleBar_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        if (_dragStartWasMaximized)
        {
            // Restoring first, then re-anchoring the drag origin at the cursor's current screen
            // position, is what makes "drag away from maximized" feel like dragging the actual
            // window instead of snapping it somewhere unrelated to where the mouse is.
            Point cursor = Cursor.Position;
            _form.WindowState = FormWindowState.Normal;
            _dragStartWasMaximized = false;
            _dragStartMouseScreen = cursor;
            _dragStartFormLocation = new Point(cursor.X - _form.Width / 2, cursor.Y);
            _form.Location = _dragStartFormLocation;
            return;
        }

        Size delta = (Size)(Cursor.Position - (Size)_dragStartMouseScreen);
        _form.Location = _dragStartFormLocation + delta;
    }

    private void TitleBar_MouseUp(object? sender, MouseEventArgs e) => _dragging = false;

    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_GETMINMAXINFO = 0x0024;

    /// <summary>Forwards <c>WM_NCHITTEST</c> (resize-border detection) and <c>WM_GETMINMAXINFO</c>
    /// (clamping "maximized" to the monitor's working area instead of covering the taskbar --
    /// a borderless Form's default maximized bounds do exactly that, since the automatic
    /// non-client adjustment that normally prevents it is tied to actually having a border) from
    /// the host form's own <see cref="Control.WndProc"/> override. This can't be done via
    /// composition alone -- e.g. a helper that subclasses the form's handle itself with <see
    /// cref="NativeWindow.AssignHandle"/> -- without risking double-subclassing a handle
    /// WinForms' own <see cref="Control"/> already manages internally; routing through the
    /// host's own real <c>WndProc</c> override is the safe way to intercept these two specific
    /// messages, at the cost of every host needing a small (~4-line) override that just forwards
    /// here.
    ///
    /// Returns true if this fully handled the message -- the caller should NOT also call
    /// <c>base.WndProc</c> for it in that case, or that would overwrite <see
    /// cref="Message.Result"/>.</summary>
    public bool HandleWndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && _form.WindowState == FormWindowState.Normal)
        {
            int x = unchecked((short)(long)m.LParam);
            int y = unchecked((short)((long)m.LParam >> 16));
            Point client = _form.PointToClient(new Point(x, y));
            int hit = HitTestResize(client);
            if (hit != HTCLIENT)
            {
                m.Result = (IntPtr)hit;
                return true;
            }

            return false;
        }

        if (m.Msg == WM_GETMINMAXINFO)
        {
            ClampMaximizedBoundsToWorkingArea(m.LParam);
            return false; // still let the default handler process the (now-adjusted) structure
        }

        return false;
    }

    private int HitTestResize(Point p)
    {
        Size size = _form.ClientSize;
        bool left = p.X <= ResizeBorderMargin;
        bool right = p.X >= size.Width - ResizeBorderMargin;
        bool top = p.Y <= ResizeBorderMargin;
        bool bottom = p.Y >= size.Height - ResizeBorderMargin;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCLIENT;
    }

    private void ClampMaximizedBoundsToWorkingArea(IntPtr lParam)
    {
        var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        Rectangle workingArea = Screen.FromHandle(_form.Handle).WorkingArea;
        Rectangle monitorBounds = Screen.FromHandle(_form.Handle).Bounds;

        info.ptMaxPosition.X = workingArea.Left - monitorBounds.Left;
        info.ptMaxPosition.Y = workingArea.Top - monitorBounds.Top;
        info.ptMaxSize.X = workingArea.Width;
        info.ptMaxSize.Y = workingArea.Height;

        Marshal.StructureToPtr(info, lParam, false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
