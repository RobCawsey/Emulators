using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GenesisSharp.Core;
using Cpu68000Bus = GenesisSharp.Cpu68000.IBus;
using Z80Bus = GenesisSharp.CpuZ80.IBus;

namespace GenesisSharp.Frontend;

/// <summary>Non-modal debug window: live CPU/VDP register readout, a VRAM tile viewer (all
/// 2048 tiles, colorized against a selectable CRAM palette line), and pause/step controls that
/// call back into <see cref="MainForm"/> (the actual timer and console lifecycle stay owned by
/// the main window; this is a read-only view plus a thin remote control).</summary>
public sealed class DebugForm : Form
{
    private const int TileSize = 8;
    private const int TileZoom = 3;
    private const int TileCount = Vdp.VramSize / 32; // 32 bytes/tile
    private const int DisasmLineCount = 24;

    private const string HelpText =
        "This window is a live view into whatever console is running in the\r\n" +
        "main window -- it doesn't own emulation itself, so closing it\r\n" +
        "(the X button) never stops or resets anything; it just hides.\r\n" +
        "\r\n" +
        "TOOLBAR\r\n" +
        "\r\n" +
        "  Pause / Resume\r\n" +
        "    The same pause state as the main window's Debug menu --\r\n" +
        "    pausing here pauses the emulator everywhere, and vice versa.\r\n" +
        "\r\n" +
        "  Step Frame\r\n" +
        "    Only does anything while paused. Advances exactly one video\r\n" +
        "    frame -- the CPU, sound chips, and video hardware all move\r\n" +
        "    forward together.\r\n" +
        "\r\n" +
        "  Step Instruction\r\n" +
        "    Only does anything while paused. Executes a single 68000\r\n" +
        "    instruction. The Z80 and video hardware do not advance --\r\n" +
        "    this is for inspecting exactly where the main CPU is\r\n" +
        "    mid-frame, not a full-system step.\r\n" +
        "\r\n" +
        "CPU TAB\r\n" +
        "\r\n" +
        "  Live 68000 and Z80 registers, plus a scrolling disassembly\r\n" +
        "  listing for each CPU starting at its current program counter.\r\n" +
        "  Refreshes automatically every frame while this window is\r\n" +
        "  visible.\r\n" +
        "\r\n" +
        "32X TAB\r\n" +
        "\r\n" +
        "  Live registers and a scrolling disassembly listing for both\r\n" +
        "  SH-2 cores (master/slave), plus the adapter's nRES/ADEN\r\n" +
        "  release state. Shown whether or not the loaded ROM is a 32X\r\n" +
        "  title -- for a non-32X ROM both cores simply sit held at\r\n" +
        "  reset and never step.\r\n" +
        "\r\n" +
        "VRAM TAB\r\n" +
        "\r\n" +
        "  A palette-line selector (0-3) and a zoomed-in view of every\r\n" +
        "  tile currently in video RAM, decoded against whichever\r\n" +
        "  palette line is selected above it.\r\n";

    private GenesisConsole? _console;
    private readonly Action _onPauseToggle;
    private readonly Action _onStepFrame;
    private readonly Action _onStepInstruction;
    private readonly Action<uint?> _onSetBreakpoint;
    private readonly Action<byte> _onSetVersionRegisterOverride;

    private readonly TextBox _registersText;
    private readonly TextBox _m68kDisasmText;
    private readonly TextBox _z80DisasmText;
    private readonly TextBox _m68kPeekAddressBox;
    private readonly TextBox _hexDumpText;

    /// <summary>When set, the 68000 disassembly panel shows a fixed range starting here instead
    /// of following the live PC -- for hunting down a caller when the "Next:" line only shows
    /// whatever subroutine happens to be executing right now (e.g. a stack-peeked return address
    /// that isn't the live PC). Cleared by the "Follow PC" button.</summary>
    private uint? _m68kPeekAddress;
    private readonly TextBox _sh2RegistersText;
    private readonly TextBox _msh2DisasmText;
    private readonly TextBox _ssh2DisasmText;
    private readonly Button _pauseButton;
    private readonly ComboBox _paletteLineSelector;
    private readonly PictureBox _palettePreview;
    private readonly Panel _vramScroll;
    private readonly PictureBox _vramPictureBox;
    private readonly Bitmap _paletteBitmap;
    private Bitmap _vramBitmap;
    private int _tilesPerRow;
    private RetroWindowChrome? _chrome;

    public DebugForm(Action onPauseToggle, Action onStepFrame, Action onStepInstruction, Action<uint?> onSetBreakpoint, Action<byte> onSetVersionRegisterOverride)
    {
        _onPauseToggle = onPauseToggle;
        _onStepFrame = onStepFrame;
        _onStepInstruction = onStepInstruction;
        _onSetBreakpoint = onSetBreakpoint;
        _onSetVersionRegisterOverride = onSetVersionRegisterOverride;

        Text = "GenesisSharp Debug";
        StartPosition = FormStartPosition.Manual;
        FormClosing += (_, e) => { e.Cancel = true; Hide(); }; // hide, don't destroy -- cheap to reopen
        RetroTheme.StyleForm(this);

        // AutoSize (rather than a guessed fixed Height) is what actually keeps these two rows
        // from clipping their own buttons/combo box -- a fixed Height that's even a few pixels
        // short of what the real control sizes need (which varies with the system font/DPI)
        // just silently overlaps the section below it instead of erroring.
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), BackColor = RetroTheme.Panel };
        _pauseButton = new Button { Text = "Pause", AutoSize = true };
        RetroTheme.StyleButton(_pauseButton);
        _pauseButton.Click += (_, _) => _onPauseToggle();
        var stepFrameButton = new Button { Text = "Step Frame", AutoSize = true };
        RetroTheme.StyleButton(stepFrameButton);
        stepFrameButton.Click += (_, _) => _onStepFrame();
        var stepInstructionButton = new Button { Text = "Step Instruction", AutoSize = true };
        RetroTheme.StyleButton(stepInstructionButton);
        stepInstructionButton.Click += (_, _) => _onStepInstruction();
        toolbar.Controls.Add(_pauseButton);
        toolbar.Controls.Add(stepFrameButton);
        toolbar.Controls.Add(stepInstructionButton);

        var registersFont = new Font(FontFamily.GenericMonospace, 10);
        // Content is currently 22 lines (68000 header + next-instruction + 4 register rows +
        // stack peek + last-exception line + blank + 6 Z80 lines (header/next-instruction/
        // main+alt register pairs/IX-IY-SP/I-R-IFF-IM) + blank + 3 VDP lines (registers + DMA
        // state) + blank + 2 audio lines) -- sized from the font's own real line height, rather
        // than a fixed pixel guess that silently clips whatever doesn't fit once font/DPI scaling
        // makes each line taller than expected (which is exactly what was cutting the Z80/VDP
        // lines off below the fold, previously).
        const int registerLineCount = 24;
        _registersText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Top,
            Height = registersFont.Height * registerLineCount + 16,
            Font = registersFont,
            BackColor = Color.Black,
            ForeColor = Color.Lime,
            ScrollBars = ScrollBars.Vertical,
        };

        // Side-by-side scrolling disassembly listings, one per CPU, each showing a labeled
        // instruction stream starting at that CPU's current PC (see M68kDisassembler/
        // Z80Disassembler.DisassembleRange and DisassemblyLabeler) -- the "annotated assembly"
        // half of turning raw opcodes into something readable, complementing the single-line
        // "Next:" instruction already in the registers box above. Dock=Fill (not a fixed
        // height) since it now lives in its own tab page below and can use whatever room the
        // CPU tab has rather than competing with the VRAM view for vertical space.
        var disasmFont = new Font(FontFamily.GenericMonospace, 9);
        // TableLayoutPanel with two 50%-width columns rather than a Left-docked fixed Width
        // plus a Fill-docked sibling: the latter split evenly only at the window's initial
        // size, since the fixed-width pane never grows -- on a larger screen (or after any
        // resize) the Fill pane absorbed all the extra space, leaving the two CPU views
        // visibly uneven. Percentage-based columns stay 50/50 across any resize.
        var disasmPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        disasmPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        disasmPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        disasmPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _m68kDisasmText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = disasmFont,
            BackColor = Color.Black,
            ForeColor = Color.Cyan,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
        };
        _z80DisasmText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = disasmFont,
            BackColor = Color.Black,
            ForeColor = Color.Yellow,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
        };
        disasmPanel.Controls.Add(_m68kDisasmText, 0, 0);
        disasmPanel.Controls.Add(_z80DisasmText, 1, 0);

        // Lets the 68000 disassembly panel show a fixed address range instead of always
        // following the live PC -- useful for reading a subroutine at an address only known from
        // a stack peek (a return address, say) rather than wherever execution happens to be right
        // now. "Go" sets the override; "Follow PC" clears it back to the normal live behavior.
        var peekRowFont = new Font(FontFamily.GenericSansSerif, 10);
        var peekRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), BackColor = RetroTheme.Panel };
        peekRow.Controls.Add(new Label { Text = "Peek 68000 addr ($):", Font = peekRowFont, ForeColor = RetroTheme.Text, AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        _m68kPeekAddressBox = new TextBox { Font = peekRowFont, Width = 80, Margin = new Padding(4, 4, 4, 4) };
        peekRow.Controls.Add(_m68kPeekAddressBox);
        var peekGoButton = new Button { Text = "Go", AutoSize = true };
        RetroTheme.StyleButton(peekGoButton);
        peekGoButton.Click += (_, _) =>
        {
            if (uint.TryParse(_m68kPeekAddressBox.Text, System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                _m68kPeekAddress = address;
                RefreshSnapshot();
            }
        };
        peekRow.Controls.Add(peekGoButton);
        var peekFollowPcButton = new Button { Text = "Follow PC", AutoSize = true };
        RetroTheme.StyleButton(peekFollowPcButton);
        peekFollowPcButton.Click += (_, _) =>
        {
            _m68kPeekAddress = null;
            RefreshSnapshot();
        };
        peekRow.Controls.Add(peekFollowPcButton);

        // Raw hex-byte peek, on any of the three buses -- disassembling a data address (rather
        // than real code) just produces plausible-looking garbage (confirmed the hard way: an
        // SDRAM comm-handshake constant disassembled as a fake "jump table" earlier in this same
        // investigation), so a genuine data value needs a raw dump, not a decoded instruction
        // listing. SDRAM in particular is SH-2-only address space -- the 68000 peek above can't
        // reach it at all -- hence the bus selector.
        var hexRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), BackColor = RetroTheme.Panel };
        hexRow.Controls.Add(new Label { Text = "Hex dump bus:", Font = peekRowFont, ForeColor = RetroTheme.Text, AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        var hexBusSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = peekRowFont,
            BackColor = RetroTheme.Panel,
            ForeColor = RetroTheme.Text,
            Width = 110,
            Margin = new Padding(4, 4, 4, 4),
        };
        hexBusSelector.Items.AddRange(new object[] { "68000", "SH-2 Master", "SH-2 Slave" });
        hexBusSelector.SelectedIndex = 0;
        hexRow.Controls.Add(hexBusSelector);
        hexRow.Controls.Add(new Label { Text = "addr ($):", Font = peekRowFont, ForeColor = RetroTheme.Text, AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        var hexAddressBox = new TextBox { Font = peekRowFont, Width = 80, Margin = new Padding(4, 4, 4, 4) };
        hexRow.Controls.Add(hexAddressBox);
        var hexDumpButton = new Button { Text = "Hex Dump", AutoSize = true };
        RetroTheme.StyleButton(hexDumpButton);
        _hexDumpText = new TextBox
        {
            Font = new Font(FontFamily.GenericMonospace, 9),
            Width = 400,
            Margin = new Padding(4, 4, 4, 4),
            ReadOnly = true,
            BackColor = Color.Black,
            ForeColor = Color.Cyan,
        };
        hexDumpButton.Click += (_, _) =>
        {
            if (_console is null || !uint.TryParse(hexAddressBox.Text, System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                return;
            }

            _hexDumpText.Text = SafeDecode(() =>
            {
                var bytes = new List<string>();
                for (uint i = 0; i < 32; i += 4)
                {
                    uint value = hexBusSelector.SelectedIndex switch
                    {
                        1 => _console.Sega32X.MasterSh2Bus.ReadLong(address + i),
                        2 => _console.Sega32X.SlaveSh2Bus.ReadLong(address + i),
                        _ => ((Cpu68000Bus)_console).ReadLong(address + i),
                    };
                    bytes.Add($"{address + i:X8}={value:X8}");
                }

                return string.Join("  ", bytes);
            });
        };
        hexRow.Controls.Add(hexDumpButton);
        hexRow.Controls.Add(_hexDumpText);

        // 68000 PC breakpoint -- pauses emulation the instant the CPU is about to fetch the
        // opcode at the given address, via M68000.InstructionFetching (a pre-existing debugging
        // hook, not new). Precise to the exact instruction, unlike polling PC between whole
        // frames/scanlines, which can easily step past the address of interest before the next
        // check runs -- needed to catch a live call stack at the exact moment of arrival rather
        // than guessing forward through unexplored subroutines from a frozen, already-stuck
        // snapshot.
        var bpRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), BackColor = RetroTheme.Panel };
        bpRow.Controls.Add(new Label { Text = "68000 breakpoint ($):", Font = peekRowFont, ForeColor = RetroTheme.Text, AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        var breakpointAddressBox = new TextBox { Font = peekRowFont, Width = 80, Margin = new Padding(4, 4, 4, 4) };
        bpRow.Controls.Add(breakpointAddressBox);
        var setBreakpointButton = new Button { Text = "Set", AutoSize = true };
        RetroTheme.StyleButton(setBreakpointButton);
        setBreakpointButton.Click += (_, _) =>
        {
            if (uint.TryParse(breakpointAddressBox.Text, System.Globalization.NumberStyles.HexNumber, null, out uint address))
            {
                _onSetBreakpoint(address);
            }
        };
        bpRow.Controls.Add(setBreakpointButton);
        var clearBreakpointButton = new Button { Text = "Clear", AutoSize = true };
        RetroTheme.StyleButton(clearBreakpointButton);
        clearBreakpointButton.Click += (_, _) => _onSetBreakpoint(null);
        bpRow.Controls.Add(clearBreakpointButton);

        // VERSION register ($A10001) live override -- GenesisConsole's own doc comment on
        // VersionRegisterValue calls this "the least-confidently-recalled constant in the whole
        // emulator": bit 6's NTSC/PAL polarity has conflicting evidence (genesis-plus-gx's own
        // REGION_USA-derived value says 0=NTSC, but a real 32X title's live boot code demands
        // the opposite for its region/hardware sanity check -- see the 32X investigation notes).
        // Only these two specific values are meaningful (the two candidate bit-6 polarities that
        // came out of that investigation, both otherwise-identical: overseas + no-expansion,
        // differing only in bit 6) -- a toggle between them, not a free-form hex box, since
        // nothing else is a real hypothesis worth typing in here. Applied immediately on click,
        // no separate "Set" step, matching how a toggle should behave. MainForm stores the value
        // (not just this form) specifically so it survives a ROM reload -- the whole point of
        // testing a boot-time hardware-detection hypothesis, which by definition needs the boot
        // sequence to actually re-run with the override already in place.
        var versionRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4), BackColor = RetroTheme.Panel };
        versionRow.Controls.Add(new Label { Text = "VERSION reg ($A10001) bit 6:", Font = peekRowFont, ForeColor = RetroTheme.Text, AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
        var versionA0RadioButton = new RadioButton
        {
            Text = "0 -- $A0 (genesis-plus-gx non-32X polarity)",
            Font = peekRowFont,
            ForeColor = RetroTheme.Text,
            BackColor = RetroTheme.Panel,
            AutoSize = true,
            Margin = new Padding(4, 6, 4, 4),
        };
        var versionE0RadioButton = new RadioButton
        {
            Text = "1 -- $E0 (32X-verified, default)",
            Font = peekRowFont,
            ForeColor = RetroTheme.Text,
            BackColor = RetroTheme.Panel,
            AutoSize = true,
            Margin = new Padding(4, 6, 4, 4),
            Checked = true,
        };
        versionA0RadioButton.Click += (_, _) => _onSetVersionRegisterOverride(0xA0);
        versionE0RadioButton.Click += (_, _) => _onSetVersionRegisterOverride(0xE0);
        versionRow.Controls.Add(versionA0RadioButton);
        versionRow.Controls.Add(versionE0RadioButton);

        // 32X tab: same registers-box-on-top, disassembly-panel-below shape as the CPU tab,
        // just for the two SH-2 cores instead of the 68000/Z80. Content is currently 17 lines
        // (32X adapter line + blank + [master header/next/4 register rows/PR-GBR-VBR-MACH-MACL
        // row] + blank + the same 7 lines for the slave) -- sized the same font-driven way as
        // _registersText above, for the same reason (avoid silently clipping under DPI scaling).
        const int sh2RegisterLineCount = 18;
        _sh2RegistersText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Top,
            Height = registersFont.Height * sh2RegisterLineCount + 16,
            Font = registersFont,
            BackColor = Color.Black,
            ForeColor = Color.Lime,
            ScrollBars = ScrollBars.Vertical,
        };
        var sh2DisasmPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        sh2DisasmPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        sh2DisasmPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        sh2DisasmPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        _msh2DisasmText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = disasmFont,
            BackColor = Color.Black,
            ForeColor = Color.Cyan,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
        };
        _ssh2DisasmText = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            Font = disasmFont,
            BackColor = Color.Black,
            ForeColor = Color.Yellow,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
        };
        sh2DisasmPanel.Controls.Add(_msh2DisasmText, 0, 0);
        sh2DisasmPanel.Controls.Add(_ssh2DisasmText, 1, 0);

        var paletteRowFont = new Font(FontFamily.GenericSansSerif, 12);
        var paletteRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6), BackColor = RetroTheme.Panel };
        paletteRow.Controls.Add(new Label { Text = "VRAM palette line:", Font = paletteRowFont, ForeColor = RetroTheme.Text, AutoSize = true, Margin = new Padding(4, 10, 6, 0) });
        _paletteLineSelector = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = paletteRowFont,
            BackColor = RetroTheme.Panel,
            ForeColor = RetroTheme.Text,
            Width = 110,
            Height = 32,
            Margin = new Padding(4, 6, 16, 6),
        };
        _paletteLineSelector.Items.AddRange(new object[] { 0, 1, 2, 3 });
        _paletteLineSelector.SelectedIndex = 0;
        _paletteLineSelector.SelectedIndexChanged += (_, _) => RefreshSnapshot();
        paletteRow.Controls.Add(_paletteLineSelector);
        _palettePreview = new PictureBox { Width = 320, Height = 32, SizeMode = PictureBoxSizeMode.StretchImage, Margin = new Padding(4, 6, 4, 6) };
        paletteRow.Controls.Add(_palettePreview);

        // Default to 128 tile columns (2048 / 128 = 16 whole rows, no ragged partial row) --
        // wide and short rather than the previous 64x32 (which read as too narrow/tall) --
        // rather than deriving it from an initial ClientSize. ClientSize itself is chosen below
        // from this bitmap's real size, specifically so the whole grid is visible without
        // scrolling the moment the window opens. Resizing the window still reflows it via
        // OnVramScrollResized, wired up further below.
        _tilesPerRow = 128;
        _vramBitmap = CreateVramBitmap(_tilesPerRow);
        _vramPictureBox = new PictureBox
        {
            Image = _vramBitmap,
            Width = _vramBitmap.Width,
            Height = _vramBitmap.Height,
            SizeMode = PictureBoxSizeMode.AutoSize,
        };
        _vramScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = Color.DimGray };
        _vramScroll.Controls.Add(_vramPictureBox);

        _paletteBitmap = new Bitmap(16, 4, PixelFormat.Format24bppRgb);

        // Two tabs instead of stacking every section vertically: the CPU view (registers +
        // both disassembly listings) and the VRAM view (palette + tile grid) each get the
        // *entire* remaining window area while selected, rather than splitting one shared
        // column of space between them -- which is exactly what left the VRAM grid starved for
        // room once the disassembly listings were added below the registers box.
        var cpuTab = new TabPage("CPU") { BackColor = RetroTheme.Background };
        cpuTab.Controls.Add(disasmPanel);
        cpuTab.Controls.Add(_registersText);
        cpuTab.Controls.Add(peekRow);
        cpuTab.Controls.Add(hexRow);
        cpuTab.Controls.Add(bpRow);
        cpuTab.Controls.Add(versionRow);

        var sh2Tab = new TabPage("32X") { BackColor = RetroTheme.Background };
        sh2Tab.Controls.Add(sh2DisasmPanel);
        sh2Tab.Controls.Add(_sh2RegistersText);

        var vramTab = new TabPage("VRAM") { BackColor = RetroTheme.Background };
        vramTab.Controls.Add(_vramScroll);
        vramTab.Controls.Add(paletteRow);

        // TabControl's native tab strip (rounded highlight, gradient fill) is one of the more
        // distinctly "modern Windows" pieces of chrome in this window -- OwnerDrawFixed +
        // DrawTab below replaces just the tab headers with flat retro rectangles; the page
        // content area itself is unaffected.
        var tabs = new TabControl { DrawMode = TabDrawMode.OwnerDrawFixed };
        tabs.DrawItem += DrawTab;
        tabs.TabPages.Add(cpuTab);
        tabs.TabPages.Add(sh2Tab);
        tabs.TabPages.Add(vramTab);

        // Tab-strip chrome height is essentially font-driven, not size-driven, so it can be
        // measured against a throwaway size before the control is docked to its real one --
        // same trick as GetPreferredSize below, just for a metric TabControl doesn't expose
        // directly.
        tabs.Size = new Size(400, 400);
        int tabChromeHeight = Math.Max(0, tabs.Height - tabs.TabPages[0].ClientSize.Height);
        tabs.Dock = DockStyle.Fill;

        var showHelpItem = new ToolStripMenuItem("&Debug Window Reference...", null,
            (_, _) => RetroHelpDialog.Show(this, "GenesisSharp Debug — Reference", HelpText));
        // Right-aligned rather than left like every other top-level item -- the conventional
        // spot for a lone Help menu, and there's nothing to its left here to line up with
        // anyway (this MenuStrip only has the one top-level item).
        var helpMenu = new ToolStripMenuItem("&Help") { Alignment = ToolStripItemAlignment.Right };
        helpMenu.DropDownItems.Add(showHelpItem);
        var menuStrip = new MenuStrip
        {
            // Colors/borders are painted by RetroMenuRenderer (installed globally in
            // Program.cs, applies to every MenuStrip using the default RenderMode); the font
            // is set here too since layout/measurement happens before OnRender runs.
            Dock = DockStyle.Top,
            BackColor = RetroTheme.Panel,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
        };
        menuStrip.Items.Add(helpMenu);
        MainMenuStrip = menuStrip;

        Controls.Add(tabs);
        Controls.Add(toolbar);
        // Added after toolbar, not before: controls docked to the same edge are docked in
        // REVERSE of their add order (see RetroWindowChrome's remarks) -- this is what puts
        // the menu strip above the toolbar rather than below it.
        Controls.Add(menuStrip);

        // Measure the AutoSize rows' real preferred heights (font/DPI-dependent, so a constant
        // would just be another version of the same clipping bug fixed above) and size the
        // client area so whichever tab needs more room fits without needing to scroll to see
        // it (the other tab, needing less, just ends up with a bit of spare space instead).
        int menuStripHeight = menuStrip.GetPreferredSize(Size.Empty).Height;
        int toolbarHeight = toolbar.GetPreferredSize(Size.Empty).Height;
        int paletteRowHeight = paletteRow.GetPreferredSize(Size.Empty).Height;
        int peekRowHeight = peekRow.GetPreferredSize(Size.Empty).Height;
        int hexRowHeight = hexRow.GetPreferredSize(Size.Empty).Height;
        int cpuTabContentHeight = peekRowHeight + hexRowHeight + _registersText.Height + disasmFont.Height * DisasmLineCount + 16;
        int sh2TabContentHeight = _sh2RegistersText.Height + disasmFont.Height * DisasmLineCount + 16;
        int vramTabContentHeight = paletteRowHeight + _vramBitmap.Height;
        int contentHeight = menuStripHeight + toolbarHeight + tabChromeHeight + Math.Max(Math.Max(cpuTabContentHeight, sh2TabContentHeight), vramTabContentHeight);
        // Half the full grid width, on top of the vertical-fit sizing above -- at 128 columns
        // the grid itself is wider than any window needs to default to; _vramScroll's own
        // AutoScroll (already relied on for the vertical case) picks up a horizontal scrollbar
        // for the other half rather than the window needing to show it all at once.
        // Scaled down from the "fits everything without scrolling" size computed above -- 0.9 *
        // 0.8 (an initial 10% reduction, then a further 20% on top of that) -- the panels' own
        // AutoScroll (VRAM grid) and TextBox scrollbars (registers/disassembly) pick up the
        // slack, so this just trims the default footprint rather than clipping anything outright.
        // Also scaled by the monitor's DPI (see RetroTheme.GetDpiScale) -- otherwise this
        // default is the same fixed physical pixel size on a 4K/high-DPI display as on a 1080p
        // one, which reads as tiny once Windows scales everything else on screen up to match.
        const double WindowScale = 0.72;
        float dpiScale = RetroTheme.GetDpiScale(this) * RetroTheme.DefaultSizeBoost;
        int desiredWidth = (int)((_vramBitmap.Width + SystemInformation.VerticalScrollBarWidth) / 2 * WindowScale * dpiScale);
        int desiredHeight = (int)((contentHeight + 8) * WindowScale * dpiScale);

        // On a smaller display, fitting the whole VRAM grid unscrolled would push the window
        // off-screen -- cap to the working area and let the VRAM panel's own AutoScroll take
        // over rather than creating a window taller than the monitor.
        var workingArea = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(Math.Min(desiredWidth, workingArea.Width - 40), Math.Min(desiredHeight, workingArea.Height - 40));

        // Wired up only after the initial sizing above is done: docking _vramScroll (Dock=Fill)
        // during construction, before ClientSize is set to its intended value, would otherwise
        // fire this against the form's small pre-sizing width and silently replace the
        // carefully-sized default bitmap with a much smaller one -- corrupting the very
        // ClientSize computation above, since it reads _vramBitmap's dimensions. Attaching it
        // here means only genuine post-construction user resizes reach it.
        _vramScroll.Resize += (_, _) => OnVramScrollResized();

        // Must be the LAST thing this constructor does -- see RetroWindowChrome's remarks on
        // why it needs to run after every other Dock=Top control (toolbar, here) has already
        // been added. Re-clamps against the same workingArea computed above, since the chrome
        // just grew ClientSize by its own frame/title-bar overhead, which on a small display
        // could otherwise push the window past the edge the clamp above was there to prevent.
        _chrome = new RetroWindowChrome(this);
        Paint += (_, e) => _chrome.PaintFrame(e.Graphics);
        ClientSize = new Size(Math.Min(ClientSize.Width, workingArea.Width - 40), Math.Min(ClientSize.Height, workingArea.Height - 40));
    }

    /// <summary>Forwards the two window messages <see cref="RetroWindowChrome"/> needs to
    /// intercept directly (resize-border hit-testing, maximize-bounds clamping) -- see <see
    /// cref="RetroWindowChrome.HandleWndProc"/>'s remarks for why this can't be done without a
    /// small override like this one in every host form.</summary>
    protected override void WndProc(ref Message m)
    {
        // Null-checked, not just non-null by construction order: adding/docking controls during
        // this constructor can trigger early handle creation as a side effect, and Windows sends
        // messages (WM_GETMINMAXINFO in particular) to a handle the instant it exists -- possibly
        // before the constructor has reached the line that assigns _chrome. Confirmed via a real
        // crash (NullReferenceException right here) during manual testing.
        if (_chrome?.HandleWndProc(ref m) == true)
        {
            return;
        }

        base.WndProc(ref m);
    }

    /// <summary>Paints one tab header as a flat, hard-edged rectangle instead of the native
    /// rounded/gradient look -- see the comment where <see cref="TabControl.DrawItem"/> is
    /// wired up. Only the header is owner-drawn; each <see cref="TabPage"/>'s own content area
    /// is unaffected.</summary>
    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var tabControl = (TabControl)sender!;
        string text = tabControl.TabPages[e.Index].Text;
        bool selected = e.Index == tabControl.SelectedIndex;

        using var backBrush = new SolidBrush(selected ? RetroTheme.Accent : RetroTheme.Panel);
        e.Graphics.FillRectangle(backBrush, e.Bounds);
        using var borderPen = new Pen(RetroTheme.Border);
        e.Graphics.DrawRectangle(borderPen, e.Bounds.X, e.Bounds.Y, e.Bounds.Width - 1, e.Bounds.Height - 1);

        RetroTheme.ApplyPixelRenderingHints(e.Graphics);
        using var textBrush = new SolidBrush(selected ? RetroTheme.Background : RetroTheme.Text);
        SizeF textSize = e.Graphics.MeasureString(text, RetroTheme.PixelFont);
        var origin = new PointF(
            e.Bounds.Left + (e.Bounds.Width - textSize.Width) / 2,
            e.Bounds.Top + (e.Bounds.Height - textSize.Height) / 2);
        e.Graphics.DrawString(text, RetroTheme.PixelFont, textBrush, origin);
    }

    /// <summary>As many tile columns as fit in the given width, clamped to a sane range --
    /// below ~8 columns the grid gets absurdly tall and slow to scroll through; above 128 the
    /// individual tiles get lost in a sea of tiny squares even at <see cref="TileZoom"/>.</summary>
    private static int ComputeTilesPerRow(int availableWidth)
    {
        int fitted = Math.Max(1, availableWidth / (TileSize * TileZoom));
        return Math.Clamp(fitted, 8, 128);
    }

    private static Bitmap CreateVramBitmap(int tilesPerRow)
    {
        int rows = (TileCount + tilesPerRow - 1) / tilesPerRow;
        return new Bitmap(tilesPerRow * TileSize * TileZoom, rows * TileSize * TileZoom, PixelFormat.Format24bppRgb);
    }

    private void OnVramScrollResized()
    {
        int newTilesPerRow = ComputeTilesPerRow(_vramScroll.ClientSize.Width);
        if (newTilesPerRow == _tilesPerRow)
        {
            return;
        }

        _tilesPerRow = newTilesPerRow;
        _vramBitmap.Dispose();
        _vramBitmap = CreateVramBitmap(_tilesPerRow);
        _vramPictureBox.Image = _vramBitmap;
        _vramPictureBox.Width = _vramBitmap.Width;
        _vramPictureBox.Height = _vramBitmap.Height;

        // Docking _vramScroll during the constructor (before any ROM is loaded, and before
        // SetConsole has ever run) fires this same resize handler -- there's nothing to draw
        // yet, and RefreshSnapshot will redraw the freshly-recreated bitmap once a console is
        // actually set.
        if (_console is not null)
        {
            UpdateVram();
        }
    }

    /// <summary>Called whenever <see cref="MainForm"/> swaps in a different console (a fresh
    /// ROM load) -- everything below just reads from whatever this points at, so there's
    /// nothing else to reset here.</summary>
    /// <summary>Set from <see cref="GenesisSharp.Cpu68000.M68000.ExceptionRaised"/> -- a debugging
    /// hook only (see that event's own remarks), wired up here purely so a real 32X ROM
    /// unexpectedly hitting a software-detected trap (illegal instruction, CHK, TRAPV, privilege
    /// violation, a TRAP #n) shows up directly in the debug window instead of needing to be
    /// inferred from a frozen-looking "BRA *-2" and a hand-traced vector table. Fires on the
    /// emulation thread; a benign race with the UI thread reading these same fields during
    /// <see cref="RefreshSnapshot"/> is acceptable, matching how every other live property this
    /// window reads is already handled.</summary>
    private int? _lastExceptionVector;
    private uint? _lastExceptionPc;
    private int _lastExceptionCount;

    public void SetConsole(GenesisConsole? console)
    {
        if (_console is not null)
        {
            _console.Cpu.ExceptionRaised -= OnCpuExceptionRaised;
        }

        _console = console;
        _lastExceptionVector = null;
        _lastExceptionPc = null;
        _lastExceptionCount = 0;

        if (_console is not null)
        {
            _console.Cpu.ExceptionRaised += OnCpuExceptionRaised;
        }

        RefreshSnapshot();
    }

    private void OnCpuExceptionRaised(int vectorNumber, uint pc)
    {
        _lastExceptionVector = vectorNumber;
        _lastExceptionPc = pc;
        _lastExceptionCount++;
    }

    public void SetPausedLabel(bool paused) => _pauseButton.Text = paused ? "Resume" : "Pause";

    /// <summary>Repaints every section from the console's current state. Cheap enough to call
    /// after every single frame/instruction step and after every real-time timer tick while
    /// the window happens to be visible -- there's no incremental diffing, it just redraws.</summary>
    public void RefreshSnapshot()
    {
        if (!Visible)
        {
            return;
        }

        if (_console is null)
        {
            _registersText.Text = "(no ROM loaded)";
            _sh2RegistersText.Text = "(no ROM loaded)";
            return;
        }

        UpdateRegistersText();
        UpdateDisassembly();
        UpdateSh2RegistersText();
        UpdateSh2Disassembly();
        UpdatePalette();
        UpdateVram();
    }

    private void UpdateRegistersText()
    {
        var cpu = _console!.Cpu;
        var z80 = _console.SoundCpu;
        var vdp = _console.Vdp;

        // Peek reads only -- disassembling the current instruction must never itself advance
        // the program counter or otherwise perturb emulation state, so this goes through the
        // same bus interfaces the real CPUs use rather than anything that mutates them.
        string m68kInstruction = SafeDecode(() => M68kDisassembler.Decode(cpu.PC, (Cpu68000Bus)_console).Text);
        string z80Instruction = SafeDecode(() => Z80Disassembler.Decode(z80.PC, (Z80Bus)_console).Text);

        var lines = new List<string>
        {
            $"68000  PC={cpu.PC:X6}  SR={cpu.SR:X4}  Cycles={cpu.TotalCycles}",
            $"  Next: {m68kInstruction}",
        };
        for (int i = 0; i < 8; i += 4)
        {
            lines.Add($"  D{i}-D{i + 3}: {cpu.D[i]:X8} {cpu.D[i + 1]:X8} {cpu.D[i + 2]:X8} {cpu.D[i + 3]:X8}");
        }
        for (int i = 0; i < 8; i += 4)
        {
            lines.Add($"  A{i}-A{i + 3}: {cpu.A[i]:X8} {cpu.A[i + 1]:X8} {cpu.A[i + 2]:X8} {cpu.A[i + 3]:X8}");
        }

        // Peek at the top few longwords of the 68000 stack (A7) -- a quick, no-breakpoint way to
        // find a caller's return address when the "Next:" line only shows whatever subroutine is
        // currently executing. Not reliable in general (a value that happens to look like a valid
        // ROM address could just be leftover stack data, not a real return address), but useful
        // as a first guess when hunting for who's repeatedly calling something.
        string stackPeek = SafeDecode(() =>
        {
            var bus68k = (Cpu68000Bus)_console!;
            var values = new List<string>();
            for (uint offset = 0; offset < 0x18; offset += 4)
            {
                values.Add($"+{offset:X2}={bus68k.ReadLong(cpu.A[7] + offset):X8}");
            }

            return string.Join(" ", values);
        });
        lines.Add($"  Stack@A7: {stackPeek}");
        lines.Add(_lastExceptionVector.HasValue
            ? $"  LastException: vector={_lastExceptionVector} PC={_lastExceptionPc:X6} count={_lastExceptionCount}"
            : "  LastException: (none)");

        lines.Add("");
        lines.Add($"Z80    PC={z80.PC:X4}  Cycles={z80.TotalCycles}  Halted={z80.Halted}");
        lines.Add($"  Next: {z80Instruction}");
        lines.Add($"  AF={z80.A:X2}{z80.F:X2}  BC={z80.B:X2}{z80.C:X2}  DE={z80.D:X2}{z80.E:X2}  HL={z80.H:X2}{z80.L:X2}");
        lines.Add($"  IX={z80.IX:X4}  IY={z80.IY:X4}  SP={z80.SP:X4}");
        lines.Add($"  AF'={z80.AltA:X2}{z80.AltF:X2} BC'={z80.AltB:X2}{z80.AltC:X2} DE'={z80.AltD:X2}{z80.AltE:X2} HL'={z80.AltH:X2}{z80.AltL:X2}");
        lines.Add($"  I={z80.I:X2}  R={z80.R:X2}  IFF1={z80.Iff1}  IFF2={z80.Iff2}  IM={z80.InterruptMode}");
        lines.Add("");
        lines.Add($"VDP    Scanline={vdp.CurrentScanline,3}  DisplayEnabled={vdp.DisplayEnabled}");
        lines.Add($"       Reg0={vdp.Registers[0]:X2} Reg1={vdp.Registers[1]:X2} Reg11={vdp.Registers[11]:X2} Reg12={vdp.Registers[12]:X2}");
        lines.Add($"       DmaMode={vdp.CurrentDmaMode}  DmaLength={vdp.DmaLength:X4}  DmaSource={vdp.DmaSourceAddress:X6}  IsDmaBusy={vdp.IsDmaBusy}  DmaBusyRemainingCycles={vdp.DmaBusyRemainingCycles}  DmaTriggerCount={vdp.DmaTriggerCount}");
        lines.Add("");
        // UnderrunCount climbing steadily during normal play (not just a handful right at
        // startup) means the thread producing audio is falling behind real-time playback --
        // each such gap is a moment of true silence, audible as a click/crackle. ClipCount
        // climbing means the opposite problem: several channels summing past full amplitude,
        // hard-clipped every time -- audible as harsh distortion rather than silence, most
        // likely on short, loud, percussive sounds. YmClipCount narrows that down to "the
        // FM channels alone, before PSG is even added" specifically.
        lines.Add($"Audio  UnderrunCount={_console!.AudioUnderrunCount}  ClipCount={_console.AudioClipCount}  YmClipCount={_console.Ym2612.ClipCount}");

        _registersText.Lines = lines.ToArray();
    }

    /// <summary>The disassemblers already fall back to a hex dump for anything they don't
    /// model, but a peek read landing right at the edge of mapped memory (e.g. PC sitting on
    /// the last byte of ROM) could still throw from the bus itself -- this is a live view that
    /// repaints every frame, so it must never crash the debug window over a momentary PC value.</summary>
    private static string SafeDecode(Func<string> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex)
        {
            return $"(decode error: {ex.Message})";
        }
    }

    private static IReadOnlyList<string> SafeDecodeLines(Func<IReadOnlyList<string>> decode)
    {
        try
        {
            return decode();
        }
        catch (Exception ex)
        {
            return new[] { $"(decode error: {ex.Message})" };
        }
    }

    /// <summary>Labeled instruction-stream listing for both CPUs, starting at each one's
    /// current PC -- the fuller complement to the single "Next:" line in the registers box:
    /// symbolic branch labels and named hardware register operands turn a run of raw opcodes
    /// into something closer to readable assembly (see <see cref="DisassemblyLabeler"/> and each
    /// disassembler's hardware-annotation table).</summary>
    private void UpdateDisassembly()
    {
        var cpu = _console!.Cpu;
        var z80 = _console.SoundCpu;

        uint m68kDisasmStart = _m68kPeekAddress ?? cpu.PC;
        var m68kLines = SafeDecodeLines(() =>
        {
            var instructions = M68kDisassembler.DisassembleRange(m68kDisasmStart, DisasmLineCount, (Cpu68000Bus)_console);
            return DisassemblyLabeler.FormatWithLabels(instructions, cpu.PC, a => a.ToString("X6"));
        });
        var z80Lines = SafeDecodeLines(() =>
        {
            var instructions = Z80Disassembler.DisassembleRange(z80.PC, DisasmLineCount, (Z80Bus)_console);
            return DisassemblyLabeler.FormatWithLabels(instructions, z80.PC, a => a.ToString("X4"));
        });

        _m68kDisasmText.Lines = m68kLines.ToArray();
        _z80DisasmText.Lines = z80Lines.ToArray();
    }

    private void UpdateSh2RegistersText()
    {
        var sega32X = _console!.Sega32X;
        var master = sega32X.MasterSh2;
        var slave = sega32X.SlaveSh2;

        // Peek reads only, same reasoning as UpdateRegistersText -- disassembling the current
        // instruction must never itself advance PC or otherwise perturb emulation state.
        string masterInstruction = SafeDecode(() => Sh2Disassembler.Decode(master.PC, sega32X.MasterSh2Bus).Text);
        string slaveInstruction = SafeDecode(() => Sh2Disassembler.Decode(slave.PC, sega32X.SlaveSh2Bus).Text);

        var lines = new List<string>
        {
            $"32X    nRES={sega32X.NRes}  ADEN={sega32X.Aden}",
            "",
            $"Master PC={master.PC:X8}  SR={master.SR:X8}  Cycles={master.TotalCycles}",
            $"  Next: {masterInstruction}",
        };
        for (int i = 0; i < 16; i += 4)
        {
            lines.Add($"  R{i}-R{i + 3}: {master.R[i]:X8} {master.R[i + 1]:X8} {master.R[i + 2]:X8} {master.R[i + 3]:X8}");
        }
        lines.Add($"  PR={master.PR:X8}  GBR={master.GBR:X8}  VBR={master.VBR:X8}  MACH={master.MACH:X8}  MACL={master.MACL:X8}");

        lines.Add("");
        lines.Add($"Slave  PC={slave.PC:X8}  SR={slave.SR:X8}  Cycles={slave.TotalCycles}");
        lines.Add($"  Next: {slaveInstruction}");
        for (int i = 0; i < 16; i += 4)
        {
            lines.Add($"  R{i}-R{i + 3}: {slave.R[i]:X8} {slave.R[i + 1]:X8} {slave.R[i + 2]:X8} {slave.R[i + 3]:X8}");
        }
        lines.Add($"  PR={slave.PR:X8}  GBR={slave.GBR:X8}  VBR={slave.VBR:X8}  MACH={slave.MACH:X8}  MACL={slave.MACL:X8}");

        _sh2RegistersText.Lines = lines.ToArray();
    }

    /// <summary>Labeled instruction-stream listing for both SH-2 cores -- same shape as
    /// <see cref="UpdateDisassembly"/>, using <see cref="Sega32X.MasterSh2Bus"/>/
    /// <see cref="Sega32X.SlaveSh2Bus"/> (the same bus each core executes against, exposed
    /// read-only for exactly this peek-disassembly purpose) rather than a bus this form owns
    /// itself.</summary>
    private void UpdateSh2Disassembly()
    {
        var sega32X = _console!.Sega32X;
        var master = sega32X.MasterSh2;
        var slave = sega32X.SlaveSh2;

        var masterLines = SafeDecodeLines(() =>
        {
            var instructions = Sh2Disassembler.DisassembleRange(master.PC, DisasmLineCount, sega32X.MasterSh2Bus);
            return DisassemblyLabeler.FormatWithLabels(instructions, master.PC, a => a.ToString("X8"));
        });
        var slaveLines = SafeDecodeLines(() =>
        {
            var instructions = Sh2Disassembler.DisassembleRange(slave.PC, DisasmLineCount, sega32X.SlaveSh2Bus);
            return DisassemblyLabeler.FormatWithLabels(instructions, slave.PC, a => a.ToString("X8"));
        });

        _msh2DisasmText.Lines = masterLines.ToArray();
        _ssh2DisasmText.Lines = slaveLines.ToArray();
    }

    private void UpdatePalette()
    {
        var data = _paletteBitmap.LockBits(new Rectangle(0, 0, 16, 4), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int row = 0; row < 4; row++)
            {
                for (int col = 0; col < 16; col++)
                {
                    var (r, g, b) = Vdp.DecodeColor(_console!.Vdp.Cram[row * 16 + col]);
                    var offset = data.Scan0 + row * data.Stride + col * 3;
                    Marshal.WriteByte(offset, 0, b);
                    Marshal.WriteByte(offset, 1, g);
                    Marshal.WriteByte(offset, 2, r);
                }
            }
        }
        finally
        {
            _paletteBitmap.UnlockBits(data);
        }

        _palettePreview.Image = _paletteBitmap;
    }

    /// <summary>Draws every tile in VRAM (2048 of them, 32 bytes/tile) as an 8x8 block,
    /// colorized against the palette line chosen in <see cref="_paletteLineSelector"/>. Genesis
    /// tile data is 4bpp, two pixels per byte (high nibble first) -- the same layout used
    /// throughout this session's headless VRAM-dump scratch scripts.</summary>
    private void UpdateVram()
    {
        int paletteLine = (int)_paletteLineSelector.SelectedItem!;
        byte[] vram = _console!.Vdp.Vram;
        ushort[] cram = _console.Vdp.Cram;

        var data = _vramBitmap.LockBits(new Rectangle(0, 0, _vramBitmap.Width, _vramBitmap.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (int tile = 0; tile < TileCount; tile++)
            {
                int tileCol = tile % _tilesPerRow;
                int tileRow = tile / _tilesPerRow;
                uint tileBase = (uint)(tile * 32);

                for (int py = 0; py < TileSize; py++)
                {
                    for (int px = 0; px < TileSize; px++)
                    {
                        byte b = vram[tileBase + py * 4 + px / 2];
                        int colorIndex = (px % 2 == 0) ? (b >> 4) : (b & 0x0F);
                        var (r, g, bl) = Vdp.DecodeColor(cram[paletteLine * 16 + colorIndex]);

                        int baseX = tileCol * TileSize * TileZoom + px * TileZoom;
                        int baseY = tileRow * TileSize * TileZoom + py * TileZoom;
                        for (int zy = 0; zy < TileZoom; zy++)
                        {
                            var rowPtr = data.Scan0 + (baseY + zy) * data.Stride + baseX * 3;
                            for (int zx = 0; zx < TileZoom; zx++)
                            {
                                var offset = rowPtr + zx * 3;
                                Marshal.WriteByte(offset, 0, bl);
                                Marshal.WriteByte(offset, 1, g);
                                Marshal.WriteByte(offset, 2, r);
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            _vramBitmap.UnlockBits(data);
        }

        _vramPictureBox.Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _vramBitmap.Dispose();
            _paletteBitmap.Dispose();
        }

        base.Dispose(disposing);
    }
}
