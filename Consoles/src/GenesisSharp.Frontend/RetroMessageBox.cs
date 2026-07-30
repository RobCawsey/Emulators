namespace GenesisSharp.Frontend;

/// <summary>A small modal replacement for <see cref="MessageBox"/>, styled to match <see
/// cref="RetroTheme"/>. The real <see cref="MessageBox"/> is OS-drawn chrome and can't be
/// recolored from managed code, so a "retro dialog" for this app's own error/confirmation prompts
/// has to be a custom <see cref="Form"/> instead. Deliberately supports only the button/icon
/// combinations <see cref="MainForm"/> actually uses (OK, and Yes/No) rather than reproducing
/// MessageBox's full API surface -- this is a purpose-built stand-in, not a general-purpose
/// control.
///
/// <see cref="FormBorderStyle.None"/> drops the native title bar/border entirely -- everything
/// visible (the title strip, the close glyph, the outer frame) is painted or laid out by this
/// class instead, which is what lets it look like a rounded, glossy-gradient window frame (see
/// <see cref="RetroTheme.DrawGradientBorder"/>) rather than a Windows-11-style dialog with
/// retro-colored insides. That trade means a few things Windows normally provides for free have
/// to be reimplemented by hand: dragging the window (<see cref="TitleBar_MouseDown"/>/<see
/// cref="TitleBar_MouseMove"/>), a close control (the "X" label in the title bar), and the
/// rounded corners themselves -- there's no "rounded window" property, so <see
/// cref="ApplyRoundedRegion"/> clips the whole window to a rounded-rectangle <see
/// cref="Region"/> instead. <see cref="AcceptButton"/>/<see cref="CancelButton"/> (Enter/Escape)
/// still work without any extra code -- those aren't tied to the native border.
///
/// File pickers (<see cref="OpenFileDialog"/>/<see cref="SaveFileDialog"/>) are the other
/// "dialog" a user sees from this app and are, for the same reason, still native/unskinned --
/// replacing those would mean writing a whole custom file browser, a much larger undertaking than
/// this app's own message prompts.</summary>
internal sealed class RetroMessageBox : Form
{
    private DialogResult _result = DialogResult.None;
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

    private RetroMessageBox(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        RetroTheme.StyleForm(this);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        Font = RetroTheme.PixelFont;
        // Leaves room for the painted gradient frame (see OnPaint) on all four sides -- the
        // docked title bar/message/button rows below sit inside this padding, not flush against
        // the form's actual edge.
        Padding = new Padding(RetroTheme.FrameThickness + 1);

        _titleBarHeight = (int)(28 * RetroTheme.GetDpiScale(this));
        _closeButtonWidth = Math.Max(_titleBarHeight + 4, TextRenderer.MeasureText("X", RetroTheme.PixelFont).Width + 16);

        _titleBar = BuildTitleBar(caption);

        var glyph = new Label
        {
            Text = IconGlyph(icon),
            Font = RetroTheme.PixelFontLarge,
            ForeColor = RetroTheme.Border,
            AutoSize = true,
            Margin = new Padding(16, 20, 8, 8),
        };

        // Wrap width scaled by the monitor's DPI (see RetroTheme.GetDpiScale) -- otherwise this
        // stays the same fixed physical pixel width on a high-DPI display while the text itself
        // renders larger, wrapping into a narrower-looking, oddly tall box.
        int messageMaxWidth = (int)(380 * RetroTheme.GetDpiScale(this) * RetroTheme.DefaultSizeBoost);
        var messageLabel = new Label
        {
            Text = text,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
            AutoSize = true,
            MaximumSize = new Size(messageMaxWidth, 0),
            Margin = new Padding(8, 20, 20, 8),
        };

        var messageRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            BackColor = RetroTheme.Background,
        };
        messageRow.Controls.Add(glyph);
        messageRow.Controls.Add(messageLabel);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(12),
            BackColor = RetroTheme.Background,
        };

        foreach ((string label, DialogResult buttonResult) in ButtonsFor(buttons))
        {
            var button = new Button { Text = label, AutoSize = true, MinimumSize = new Size(84, 30) };
            RetroTheme.StyleButton(button);
            button.Click += (_, _) =>
            {
                _result = buttonResult;
                Close();
            };
            buttonRow.Controls.Add(button);

            if (buttonResult is DialogResult.OK or DialogResult.Yes)
            {
                AcceptButton = button;
            }
            else
            {
                CancelButton = button;
            }
        }

        // Counterintuitively, controls docked to the same edge are docked in REVERSE of their
        // add order -- the LAST one added ends up CLOSEST to that edge (this is documented
        // .NET behavior, not a guess). titleBar must therefore be added last among the
        // Top-docked group, or it lands below messageRow instead of above it -- which is
        // exactly the bug a previous version of this file had.
        Controls.Add(buttonRow);
        Controls.Add(messageRow);
        Controls.Add(_titleBar);

        // Form.AutoSize=true doesn't reliably size a Form to fit several stacked Dock=Top/
        // Bottom children (it's a known-fragile combination once more than one docked child is
        // involved) -- DebugForm works around the same limitation by computing its own
        // ClientSize from its parts' GetPreferredSize, and this does the same rather than
        // trusting AutoSize to reflow correctly, which is what previously left the message text
        // clipped instead of sized to fit.
        Size messageSize = messageRow.GetPreferredSize(Size.Empty);
        Size buttonSize = buttonRow.GetPreferredSize(Size.Empty);
        const int minContentWidth = 300;
        int contentWidth = Math.Max(minContentWidth, Math.Max(messageSize.Width, buttonSize.Width));
        int contentHeight = _titleBarHeight + messageSize.Height + buttonSize.Height;
        ClientSize = new Size(contentWidth + Padding.Horizontal, contentHeight + Padding.Vertical);
        ApplyRoundedRegion();
        Resize += (_, _) => ApplyRoundedRegion();
    }

    /// <summary>Clips the entire window -- chrome and all -- to a rounded-rectangle <see
    /// cref="Region"/> matching <see cref="RetroTheme.CreateRoundedRectanglePath"/>'s corner
    /// radius. Re-applied on every <see cref="Resize"/> rather than just once in the constructor:
    /// this dialog's size is fixed after construction in practice, but a Region computed against
    /// a size that later changes would leave stale square corners, so re-deriving it from the
    /// current <see cref="Control.ClientSize"/> is cheap insurance against that.</summary>
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

    /// <summary>The painted stand-in for the native title bar: a strip with the dialog's caption
    /// and a small close glyph, draggable by holding the mouse down anywhere
    /// on it (see <see cref="TitleBar_MouseDown"/>).</summary>
    private Panel BuildTitleBar(string caption)
    {
        // A shade lighter than the menu strip's own Panel color -- distinct enough to read as
        // its own strip while still clearly part of the same color family.
        var titleBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = _titleBarHeight,
            BackColor = RetroTheme.PanelLight,
        };
        titleBar.Paint += (_, e) =>
        {
            // A single darker line along the bottom edge is enough to read as a seam between
            // the title bar and the body below, without needing a second child control.
            using var seam = new Pen(RetroTheme.Background, 2);
            e.Graphics.DrawLine(seam, 0, titleBar.Height - 1, titleBar.Width, titleBar.Height - 1);
        };

        var titleLabel = new Label
        {
            Text = caption,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
        };

        var closeButton = new Label
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
        closeButton.Click += (_, _) => Close(); // leaves _result at None -> Show() maps that to Cancel
        closeButton.MouseEnter += (_, _) => closeButton.BackColor = Color.FromArgb(200, 32, 64);
        closeButton.MouseLeave += (_, _) => closeButton.BackColor = titleBar.BackColor;

        // Dragging needs to work from a mouse-down on the bar itself OR on the caption label
        // that fills most of it -- not the close button, which has its own click handler.
        titleBar.MouseDown += TitleBar_MouseDown;
        titleBar.MouseMove += TitleBar_MouseMove;
        titleBar.MouseUp += TitleBar_MouseUp;
        titleLabel.MouseDown += TitleBar_MouseDown;
        titleLabel.MouseMove += TitleBar_MouseMove;
        titleLabel.MouseUp += TitleBar_MouseUp;

        titleBar.Controls.Add(titleLabel);
        titleBar.Controls.Add(closeButton);
        return titleBar;
    }

    /// <summary>Tracked in screen coordinates rather than sender-relative ones deliberately --
    /// the mouse events fire from either the title bar panel or its child label, each with its
    /// own coordinate origin, so screen-space avoids having to know which one raised this.</summary>
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

    private static (string Label, DialogResult Result)[] ButtonsFor(MessageBoxButtons buttons) => buttons switch
    {
        MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
        _ => new[] { ("OK", DialogResult.OK) },
    };

    private static string IconGlyph(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => "[ X ]",
        MessageBoxIcon.Warning => "[ ? ]",
        _ => "[ i ]",
    };

    /// <summary>Paints the rounded, glossy-gradient frame that stands in for the native window
    /// border -- see <see cref="RetroTheme.DrawGradientBorder"/>.</summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        RetroTheme.DrawGradientBorder(e.Graphics, ClientRectangle);
    }

    /// <summary>Mirrors the subset of <see cref="MessageBox.Show"/>'s overload used by <see
    /// cref="MainForm"/>, so the call sites there only needed their class name changed.</summary>
    public static DialogResult Show(IWin32Window owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        using var box = new RetroMessageBox(text, caption, buttons, icon);
        box.ShowDialog(owner);
        return box._result == DialogResult.None ? DialogResult.Cancel : box._result;
    }
}
