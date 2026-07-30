namespace GenesisSharp.Frontend;

/// <summary>A fixed-size, non-resizable modal showing a scrollable block of reference text --
/// used for each window's Help menu. Styled the same way as <see cref="RetroMessageBox"/> (custom
/// title bar, hand-rolled drag, rounded glossy-gradient frame via <see
/// cref="RetroTheme.DrawGradientBorder"/>) and for the same reason that class gives for not using
/// <see cref="RetroWindowChrome"/>: this is a fixed-size dialog, not a resizable window, so the
/// heavier WM_NCHITTEST/WM_GETMINMAXINFO machinery that class exists for has nothing to do here.
/// The one difference from <see cref="RetroMessageBox"/> is a scrollable read-only <see
/// cref="TextBox"/> instead of a short label -- help text is long enough that wrapping it into a
/// message-box-sized label would either clip or produce an unreasonably tall window.</summary>
internal sealed class RetroHelpDialog : Form
{
    private readonly Panel _titleBar;

    /// <summary>DPI-scaled (see <see cref="RetroTheme.GetDpiScale"/>) for the same reason this
    /// dialog's overall default size is -- a fixed pixel height here would look
    /// disproportionately thin on a high-DPI display, the same issue previously fixed for
    /// <see cref="RetroWindowChrome"/>'s own title bar. This dialog has no menu strip to match
    /// heights against instead, unlike that class.</summary>
    private readonly int _titleBarHeight;

    /// <summary>Wide enough for the "X" close glyph without wrapping -- <see
    /// cref="BuildTitleBar"/> turns <c>AutoSize</c> off on that Label, and a Label that doesn't
    /// fit its text wraps instead of clipping it, which is exactly what caused a real
    /// misalignment bug in <see cref="RetroWindowChrome"/>'s own title-bar buttons.</summary>
    private readonly int _closeButtonWidth;

    private bool _dragging;
    private Point _dragStartMouseScreen;
    private Point _dragStartFormLocation;

    private RetroHelpDialog(string title, string content)
    {
        RetroTheme.StyleForm(this);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = RetroTheme.PixelFont;
        Padding = new Padding(RetroTheme.FrameThickness + 1);

        _titleBarHeight = (int)(28 * RetroTheme.GetDpiScale(this));
        _closeButtonWidth = Math.Max(_titleBarHeight + 4, TextRenderer.MeasureText("X", RetroTheme.PixelFont).Width + 16);

        // Generous default -- the help text is hand-wrapped assuming roughly this much width;
        // a narrower box forces WordWrap to re-break those already-sensible line breaks a
        // second time, which is what made the dialog read as cramped/"bunched up." Scaled by
        // the monitor's DPI (see RetroTheme.GetDpiScale) so this default opens at a comparable
        // apparent size on a high-DPI display, then clamped against the screen's working area
        // (same pattern DebugForm uses) so it still can't exceed a small display --
        // Screen.FromControl falls back to the primary screen safely if this Form's handle
        // doesn't exist yet, which it doesn't at this point.
        float dpiScale = RetroTheme.GetDpiScale(this) * RetroTheme.DefaultSizeBoost;
        var workingArea = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(
            Math.Min((int)(780 * dpiScale), workingArea.Width - 40),
            Math.Min((int)(620 * dpiScale), workingArea.Height - 40));

        _titleBar = BuildTitleBar(title);

        var closeButton = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(90, 30) };
        RetroTheme.StyleButton(closeButton);
        closeButton.Click += (_, _) => Close();
        AcceptButton = closeButton;
        CancelButton = closeButton;

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            BackColor = RetroTheme.Background,
        };
        buttonRow.Controls.Add(closeButton);

        var contentBox = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = RetroTheme.Background,
            ForeColor = RetroTheme.Text,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            Text = content,
            TabStop = false,
        };

        // A multiline+WordWrap TextBox's native scroll range is computed against whatever size
        // it has at the moment Text is assigned -- here, that's still its default pre-layout
        // size (Text is set in the object initializer above, before this control is ever added
        // to the form and resized by Dock=Fill). The visible wrapping catches up once it's
        // resized, but the scrollable range can end up stale, silently capping how far down the
        // user can actually scroll short of the true end of the text -- confirmed via a real
        // report that this dialog's help text cut off before the last section. Reassigning Text
        // once the dialog has its real, final size forces the native control to recompute the
        // wrap points and scroll range from scratch against that size.
        Shown += (_, _) => contentBox.Text = content;

        // Same reverse-add-order rule as everywhere else in this frontend's custom chrome:
        // among controls sharing an edge, the LAST one added ends up CLOSEST to it. Only
        // titleBar/buttonRow share edges with anything here (Top/Bottom respectively), each
        // alone on their edge, so plain add order is fine -- titleBar just needs to be added
        // after nothing else claims the top edge, which is already true.
        Controls.Add(buttonRow);
        Controls.Add(contentBox);
        Controls.Add(_titleBar);

        ApplyRoundedRegion();
        Resize += (_, _) => ApplyRoundedRegion();
    }

    private Panel BuildTitleBar(string title)
    {
        // A shade lighter than the menu strip's own Panel color -- distinct enough to read as
        // its own strip while still clearly part of the same color family.
        var titleBar = new Panel { Dock = DockStyle.Top, Height = _titleBarHeight, BackColor = RetroTheme.PanelLight };
        titleBar.Paint += (_, e) =>
        {
            using var seam = new Pen(RetroTheme.Background, 2);
            e.Graphics.DrawLine(seam, 0, titleBar.Height - 1, titleBar.Width, titleBar.Height - 1);
        };

        var titleLabel = new Label
        {
            Text = title,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        var closeGlyph = new Label
        {
            Text = "X",
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
            Dock = DockStyle.Right,
            // Off, not the Label default of true -- AutoSize fights Dock=Right + the explicit
            // Width below, which is what caused a real vertical-misalignment bug in
            // RetroWindowChrome's own title-bar buttons.
            AutoSize = false,
            Width = _closeButtonWidth,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand,
        };
        closeGlyph.Click += (_, _) => Close();
        closeGlyph.MouseEnter += (_, _) => closeGlyph.BackColor = Color.FromArgb(200, 32, 64);
        closeGlyph.MouseLeave += (_, _) => closeGlyph.BackColor = titleBar.BackColor;

        titleBar.MouseDown += TitleBar_MouseDown;
        titleBar.MouseMove += TitleBar_MouseMove;
        titleBar.MouseUp += TitleBar_MouseUp;
        titleLabel.MouseDown += TitleBar_MouseDown;
        titleLabel.MouseMove += TitleBar_MouseMove;
        titleLabel.MouseUp += TitleBar_MouseUp;

        titleBar.Controls.Add(titleLabel);
        titleBar.Controls.Add(closeGlyph);
        return titleBar;
    }

    private void TitleBar_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _dragging = true;
        _dragStartMouseScreen = Cursor.Position;
        _dragStartFormLocation = Location;
    }

    private void TitleBar_MouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        Size delta = (Size)(Cursor.Position - (Size)_dragStartMouseScreen);
        Location = _dragStartFormLocation + delta;
    }

    private void TitleBar_MouseUp(object? sender, MouseEventArgs e) => _dragging = false;

    private void ApplyRoundedRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            return;
        }

        using var path = RetroTheme.CreateRoundedRectanglePath(new Rectangle(Point.Empty, ClientSize), RetroTheme.CornerRadius);
        Region = new Region(path);

        // The title bar's own top corners need the same rounding -- otherwise its flat
        // rectangular corners cut a square notch across the window's rounded curve instead of
        // continuing it. Uses the border's INNER radius (the curve bounding the content area,
        // not the window's outer edge), since that's the curve the title bar actually sits
        // against.
        if (_titleBar.Width > 0 && _titleBar.Height > 0)
        {
            int innerRadius = Math.Max(0, RetroTheme.CornerRadius - RetroTheme.FrameThickness);
            using var titleBarPath = RetroTheme.CreateTopRoundedRectanglePath(new Rectangle(Point.Empty, _titleBar.Size), innerRadius);
            _titleBar.Region = new Region(titleBarPath);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RetroTheme.DrawGradientBorder(e.Graphics, ClientRectangle);
    }

    public static void Show(IWin32Window owner, string title, string content)
    {
        using var dialog = new RetroHelpDialog(title, content);
        dialog.ShowDialog(owner);
    }
}
