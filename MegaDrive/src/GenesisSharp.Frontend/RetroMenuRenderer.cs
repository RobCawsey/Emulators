namespace GenesisSharp.Frontend;

/// <summary>Flat, hard-edged <see cref="ToolStripRenderer"/> replacing WinForms' default
/// gradient/shadow menu look with the same palette as <see cref="RetroTheme"/>. Installed once,
/// globally, via <see cref="ToolStripManager.Renderer"/> in Program.cs -- that's what makes it
/// apply to a MenuStrip's dropdowns too, not just its top-level bar (a dropdown is a separate
/// <see cref="ToolStripDropDownMenu"/> control that reads the ToolStripManager's renderer, not
/// its owner item's).</summary>
internal sealed class RetroMenuRenderer : ToolStripProfessionalRenderer
{
    public RetroMenuRenderer() : base(new RetroColorTable())
    {
        // Rounded selection-highlight corners are exactly the kind of soft native-Windows detail
        // this theme is trying to get away from.
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is MenuStrip)
        {
            // A MenuStrip here is always docked directly under this app's own custom title bar,
            // as part of the same window chrome -- drawing a full rectangle around it made it
            // look like a separate floating box sitting inside the window instead of a
            // continuous strip of that chrome. A dropdown (below) still gets its border: unlike
            // the docked bar, it genuinely floats over other content and needs an outline to
            // read as distinct from what's behind it.
            return;
        }

        using var pen = new Pen(RetroTheme.Border, 2);
        e.Graphics.DrawRectangle(pen, 1, 1, e.ToolStrip.Width - 3, e.ToolStrip.Height - 3);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        RetroTheme.ApplyPixelRenderingHints(e.Graphics);
        e.TextFont = RetroTheme.PixelFont;
        e.TextColor = !e.Item.Enabled
            ? RetroTheme.DisabledText
            : e.Item.Selected || e.Item.Pressed ? RetroTheme.Background : RetroTheme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(RetroTheme.Border);
        int y = e.Item.Height / 2;
        e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = e.Item?.Selected == true ? RetroTheme.Background : RetroTheme.Text;
        base.OnRenderArrow(e);
    }

    protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(RetroTheme.Panel);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    /// <summary>Backs every color <see cref="ToolStripProfessionalRenderer"/> asks for -- this is
    /// what removes the default soft blue gradient selection highlight and menu-bar background in
    /// favor of flat retro colors, without having to override every individual OnRender* method
    /// the base class exposes.</summary>
    private sealed class RetroColorTable : ProfessionalColorTable
    {
        public override Color MenuStripGradientBegin => RetroTheme.Panel;
        public override Color MenuStripGradientEnd => RetroTheme.Panel;
        public override Color MenuItemSelected => RetroTheme.Accent;
        public override Color MenuItemSelectedGradientBegin => RetroTheme.Accent;
        public override Color MenuItemSelectedGradientEnd => RetroTheme.Accent;
        public override Color MenuItemPressedGradientBegin => RetroTheme.Border;
        public override Color MenuItemPressedGradientEnd => RetroTheme.Border;
        public override Color MenuItemBorder => RetroTheme.Border;
        public override Color MenuBorder => RetroTheme.Border;
        public override Color ImageMarginGradientBegin => RetroTheme.Panel;
        public override Color ImageMarginGradientMiddle => RetroTheme.Panel;
        public override Color ImageMarginGradientEnd => RetroTheme.Panel;
        public override Color ToolStripDropDownBackground => RetroTheme.Panel;
        public override Color SeparatorDark => RetroTheme.Border;
        public override Color SeparatorLight => RetroTheme.Border;
    }
}
