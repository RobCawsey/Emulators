using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using GenesisSharp.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GenesisSharp.Frontend;

/// <summary>Renders the emulated console's frame buffer, forwards keyboard input to controller
/// port 1, and plays audio through NAudio/WASAPI.
///
/// Emulation (<see cref="RunEmulationLoop"/>) runs on its own dedicated background thread, paced
/// by a <see cref="Stopwatch"/>-based fixed timestep rather than a WinForms <see
/// cref="System.Windows.Forms.Timer"/> -- an earlier version of this ran emulation on the UI
/// thread's own timer, which shares that thread with rendering and the Windows message loop.
/// Real-ROM testing turned up a live <c>AudioUnderrunCount</c> that climbed continuously during
/// normal play: whenever the UI thread's other responsibilities delayed a timer tick, the audio
/// buffer (filled inline with CPU stepping, cycle-accurately, but only whenever that tick
/// happened to fire) ran dry, and every such gap is a moment of genuine silence -- audible as a
/// click or crackle. A dedicated thread with nothing else competing for its time keeps that
/// buffer fed reliably. The UI thread's own timer is now pure rendering plus debug-window
/// refresh: it reads a small, lock-protected snapshot of the completed frame (<see
/// cref="_frameSnapshot"/>) rather than touching the emulation thread's live state directly, so
/// painting never tears a frame the emulation thread is midway through overwriting.</summary>
public sealed class MainForm : Form
{
    private const int ScaleFactor = 4;

    /// <summary>Applied on top of <see cref="ScaleFactor"/> for the window's initial size only
    /// (0.8 * 0.8 -- an initial 20% reduction, then a further 20% on top of that) -- rendering
    /// itself always stretches <see cref="_frameBitmap"/> to fill whatever size <see
    /// cref="_display"/> actually ends up (see <see cref="OnDisplayPaint"/>), so this doesn't
    /// affect the emulated picture's sharpness/aspect, just how large the window opens.</summary>
    private const double WindowScale = 0.64;

    private GenesisConsole? _console;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Bitmap _frameBitmap;
    private readonly byte[] _bitmapRowBuffer;
    private readonly DisplayPanel _display;
    private WasapiOut? _audioPlayer;
    private string? _errorMessage;
    private string? _loadedRomPath;

    /// <summary>The emulation thread's own copy of the just-completed frame, taken right after
    /// <see cref="GenesisConsole.RunFrame"/> returns (i.e. while nothing is concurrently writing
    /// <see cref="Vdp.FrameBuffer"/>) and consumed by the UI thread's own timer for painting --
    /// see the type remarks for why a direct read of the live frame buffer isn't safe here.</summary>
    private readonly byte[] _frameSnapshot = new byte[Vdp.ScreenWidth * Vdp.ScreenHeight * 3];
    private readonly object _frameSnapshotLock = new();

    private Thread? _emulationThread;
    private volatile bool _emulationThreadRunning;
    private volatile bool _stepFrameRequested;
    private volatile bool _stepInstructionRequested;

    /// <summary>How many frames the last catch-up burst rendered-but-didn't-present, i.e. how far
    /// behind real time emulation currently is. 0 whenever it's keeping up. Written by the
    /// emulation thread and read by the UI thread's status line -- a plain diagnostic, deliberately
    /// not <c>volatile</c>: a stale read just shows a slightly old number for one tick.</summary>
    private int _framesSkippedLastBurst;

    /// <summary>The window title without any "behind real time" suffix, so
    /// <see cref="RepaintAndRefreshDebugWindow"/> can add and remove that suffix without
    /// progressively eating the ROM name.</summary>
    private string _baseTitle = "GenesisSharp";

    /// <summary>The UI thread's hand-off to the emulation thread for a save/load-state request
    /// -- <see cref="GenesisConsole.SaveState"/>/<see cref="GenesisConsole.LoadState"/> touch the
    /// same mutable state <see cref="RunOneFrame"/> does, so they must run on that same thread,
    /// never concurrently from here. Set by <see cref="PerformStateOperation"/>, cleared and
    /// answered (<see cref="_stateRequestError"/>, <see cref="_stateRequestDone"/>) by <see
    /// cref="RunEmulationLoop"/> at the top of its next iteration -- checked whether paused or
    /// not, so a request lands between two whole frames either way, never mid-frame.</summary>
    private volatile bool _stateRequestPending;
    private bool _stateRequestIsSave;
    private string? _stateRequestPath;
    private bool _stateRequestAllowRomMismatch;
    private volatile bool _stateRequestDone;
    private Exception? _stateRequestError;

    /// <summary>Plain <see cref="Panel"/> can't have double-buffering turned on from outside
    /// (the property's protected), so this exists purely to flip it on and host the emulator's
    /// own <see cref="OnPaint"/> logic below the menu strip.</summary>
    private sealed class DisplayPanel : Panel
    {
        public DisplayPanel() => DoubleBuffered = true;
    }

    /// <summary>Temporary lockup-diagnosis hook: every PC the CPU has ever fetched, plus how
    /// many timer ticks it's been since that set last grew. If it goes three real seconds
    /// (~180 ticks at the ~17ms timer interval) without a single new address, that's a strong
    /// sign execution is confined to a small stuck loop rather than legitimately idling — real
    /// "wait for vblank" spins revisit the same handful of addresses every frame but still see
    /// *new* ones too as the game's main loop keeps advancing around them.</summary>
    private readonly HashSet<uint> _visitedPcs = new();
    private int _ticksSinceNewPc;
    private bool _lockupDumped;
    private bool _isClosing;

    private DebugForm? _debugForm;
    private volatile bool _paused;
    private ToolStripMenuItem _pauseMenuItem = null!;
    private RetroWindowChrome? _chrome;

    /// <summary>68000 PC breakpoint, set from <see cref="DebugForm"/>'s breakpoint row -- split
    /// into an armed flag plus a plain address (rather than a single <c>uint?</c>) because
    /// <c>volatile</c> doesn't allow nullable value types. Checked on the emulation thread
    /// against every address <c>M68000.InstructionFetching</c> reports (a pre-existing debugging
    /// hook); a match throws <see cref="BreakpointHitException"/> to unwind out of the current
    /// <see cref="GenesisConsole.RunFrame"/> call immediately -- a plain flag alone wouldn't stop
    /// execution until the whole frame (thousands of instructions) finished.</summary>
    private volatile bool _breakpointArmed;
    private volatile uint _breakpointAddress;

    /// <summary>Debug-window override for <see cref="GenesisConsole.VersionRegisterValue"/> --
    /// null means "use whatever GenesisConsole's own default is." Stored here, not just written
    /// directly to the live console, specifically so it survives <see cref="StartConsole"/>
    /// creating a brand-new <see cref="GenesisConsole"/> on reload (which would otherwise reset
    /// it back to the default via the property initializer) -- same reasoning as
    /// <see cref="_breakpointArmed"/>/<see cref="_breakpointAddress"/> surviving reloads, and the
    /// bug this field's addition fixes: setting an override, then reloading to re-run the boot
    /// sequence from scratch, silently lost it before the check it was meant to influence ever
    /// ran again.</summary>
    private byte? _versionRegisterOverride;

    /// <summary>Set by the emulation thread the moment a breakpoint fires, cleared and acted on
    /// by <see cref="RepaintAndRefreshDebugWindow"/> on the UI thread -- <see
    /// cref="ToolStripMenuItem.Text"/>/<see cref="DebugForm.SetPausedLabel"/> are WinForms
    /// controls and must only ever be touched from there, never from the emulation thread that
    /// actually detects the hit.</summary>
    private volatile bool _breakpointHit;

    private sealed class BreakpointHitException : Exception;

    private ToolStripMenuItem _retroMenuItem = null!;
    private bool _retroOverlayEnabled;

    /// <summary>Pre-composited once and reused across paints -- see <see
    /// cref="EnsureRetroOverlayBitmap"/> -- rather than rebuilding the scanline/vignette pattern
    /// with a fresh <see cref="HatchBrush"/>/<see cref="PathGradientBrush"/> on every single
    /// repaint (up to ~60/sec), which is unnecessary work since neither pattern depends on the
    /// emulated frame's own contents, only on <see cref="_display"/>'s current size.</summary>
    private Bitmap? _retroOverlayBitmap;
    private Size _retroOverlaySize;

    public MainForm(Cartridge? cartridge, string? romPath = null)
    {
        Text = "GenesisSharp";
        KeyPreview = true;
        RetroTheme.StyleForm(this);

        var menuStrip = BuildMenu();

        _display = new DisplayPanel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _display.Paint += OnDisplayPaint;

        Controls.Add(_display);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;

        // Client area = the emulator's own screen size below the menu strip (whose height
        // isn't known until it's laid out, hence reading it back after adding it above). Only
        // the display area is scaled by WindowScale -- the menu strip stays its natural height.
        // Also scaled by the monitor's DPI (see RetroTheme.GetDpiScale) -- otherwise this default
        // is the same fixed physical pixel size on a 4K/high-DPI display as on a 1080p one,
        // which reads as tiny once Windows scales everything else on screen up to match.
        float dpiScale = RetroTheme.GetDpiScale(this) * RetroTheme.DefaultSizeBoost;
        ClientSize = new Size(
            (int)(Vdp.ScreenWidth * ScaleFactor * WindowScale * dpiScale),
            (int)(Vdp.ScreenHeight * ScaleFactor * WindowScale * dpiScale) + menuStrip.Height);

        // Pure rendering + debug-window refresh now -- see the type remarks. Runs continuously
        // from construction onward rather than being started/stopped alongside the emulation
        // thread, since it no longer has anything emulation-specific to gate: with no ROM
        // loaded it just keeps repainting the "no ROM" placeholder OnDisplayPaint already draws.
        _timer = new System.Windows.Forms.Timer { Interval = 17 }; // ~59Hz is plenty for repainting; doesn't affect emulation timing at all anymore
        _timer.Tick += (_, _) => RepaintAndRefreshDebugWindow();
        _timer.Start();

        _frameBitmap = new Bitmap(Vdp.ScreenWidth, Vdp.ScreenHeight, PixelFormat.Format24bppRgb);
        _bitmapRowBuffer = new byte[Vdp.ScreenWidth * 3];

        if (cartridge is not null)
        {
            StartConsole(cartridge, romPath);
        }

        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // Must be the LAST thing this constructor does -- see RetroWindowChrome's remarks on
        // why it needs to run after every other Dock=Top control (menuStrip, here) has already
        // been added.
        _chrome = new RetroWindowChrome(this);
        Paint += (_, e) => _chrome.PaintFrame(e.Graphics);
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

    private MenuStrip BuildMenu()
    {
        var openItem = new ToolStripMenuItem("&Open ROM...", null, (_, _) => PromptAndLoadRom())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        };
        var saveStateItem = new ToolStripMenuItem("&Save State...", null, (_, _) => PromptAndSaveState())
        {
            ShortcutKeys = Keys.Control | Keys.S,
        };
        var loadStateItem = new ToolStripMenuItem("&Load State...", null, (_, _) => PromptAndLoadState())
        {
            ShortcutKeys = Keys.Control | Keys.L,
        };
        var exitItem = new ToolStripMenuItem("E&xit", null, (_, _) => Close());
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(openItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(saveStateItem);
        fileMenu.DropDownItems.Add(loadStateItem);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);

        var showDebugItem = new ToolStripMenuItem("&Show Debug Window", null, (_, _) => ShowDebugWindow())
        {
            ShortcutKeys = Keys.Control | Keys.D,
        };
        _pauseMenuItem = new ToolStripMenuItem("&Pause", null, (_, _) => TogglePause())
        {
            ShortcutKeys = Keys.F5,
        };
        var stepFrameItem = new ToolStripMenuItem("Step &Frame", null, (_, _) => StepFrame())
        {
            ShortcutKeys = Keys.F6,
        };
        var stepInstructionItem = new ToolStripMenuItem("Step &Instruction", null, (_, _) => StepInstruction())
        {
            ShortcutKeys = Keys.F7,
        };
        var debugMenu = new ToolStripMenuItem("&Debug");
        debugMenu.DropDownItems.Add(showDebugItem);
        debugMenu.DropDownItems.Add(new ToolStripSeparator());
        debugMenu.DropDownItems.Add(_pauseMenuItem);
        debugMenu.DropDownItems.Add(stepFrameItem);
        debugMenu.DropDownItems.Add(stepInstructionItem);

        // A top-level item with no DropDownItems, not a dropdown menu of its own -- clicking it
        // directly toggles the overlay (see ToggleRetroOverlay), the same one-click-does-the-
        // thing shape as _pauseMenuItem, just living at the top level instead of inside Debug.
        // The bracket glyph swaps on/off state the same way RetroWindowChrome's own maximize
        // button does ("[ ]"/"[=]") rather than relying on ToolStripMenuItem.Checked, whose
        // native checkmark rendering isn't designed for top-level MenuStrip items.
        _retroMenuItem = new ToolStripMenuItem(RetroMenuText(enabled: false), null, (_, _) => ToggleRetroOverlay());

        var menuReferenceItem = new ToolStripMenuItem("&Menu Reference...", null, (_, _) => RetroHelpDialog.Show(this, "GenesisSharp — Menu Reference", MenuReferenceText));
        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add(menuReferenceItem);

        var menuStrip = new MenuStrip
        {
            // Colors/borders are painted by RetroMenuRenderer (installed globally in Program.cs);
            // the font is set here too since layout/measurement happens before OnRender runs.
            BackColor = RetroTheme.Panel,
            ForeColor = RetroTheme.Text,
            Font = RetroTheme.PixelFont,
        };
        menuStrip.Items.Add(fileMenu);
        menuStrip.Items.Add(debugMenu);
        menuStrip.Items.Add(_retroMenuItem);
        menuStrip.Items.Add(helpMenu);
        return menuStrip;
    }

    private static string RetroMenuText(bool enabled) => enabled ? "[X] &Retro" : "[ ] &Retro";

    /// <summary>Just flips the flag <see cref="OnDisplayPaint"/> checks and repaints -- the
    /// overlay itself doesn't need rebuilding here even when turning it on, since <see
    /// cref="EnsureRetroOverlayBitmap"/> lazily builds (and caches) it the first time it's
    /// actually needed.</summary>
    private void ToggleRetroOverlay()
    {
        _retroOverlayEnabled = !_retroOverlayEnabled;
        _retroMenuItem.Text = RetroMenuText(_retroOverlayEnabled);
        _display.Invalidate();
    }

    /// <summary>Builds (or rebuilds, if <paramref name="size"/> changed since last time -- e.g.
    /// the window was resized) a semi-transparent scanline + vignette pattern sized to match, so
    /// <see cref="OnDisplayPaint"/> can composite it over the emulated frame with a single cheap
    /// <see cref="Graphics.DrawImage(Image, Rectangle)"/> rather than repainting either effect
    /// from scratch on every frame.</summary>
    private void EnsureRetroOverlayBitmap(Size size)
    {
        if (_retroOverlayBitmap is not null && _retroOverlaySize == size)
        {
            return;
        }

        _retroOverlayBitmap?.Dispose();
        _retroOverlaySize = size;
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);

        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.None; // scanlines read as CRT-sharp, not blurred

        // Scanlines: HatchBrush's built-in "LightHorizontal" pattern is a cheap, single-call
        // stand-in for manually looping over rows and drawing a line every few pixels -- exactly
        // as lightweight as this overlay is meant to be, at the cost of not being able to tune
        // the exact line spacing (GDI+ fixes that per hatch style).
        using var scanlineBrush = new HatchBrush(HatchStyle.LightHorizontal, Color.FromArgb(60, 0, 0, 0), Color.Transparent);
        g.FillRectangle(scanlineBrush, 0, 0, size.Width, size.Height);

        // Vignette: a radial PathGradientBrush centered on the display, transparent in the
        // middle and fading to semi-transparent black at the corners -- the ellipse is
        // deliberately larger than the bitmap itself (extends 30% past each edge) so the
        // darkened band sits at the corners/edges rather than the gradient's own visible ring
        // falling inside the visible area.
        using var vignettePath = new GraphicsPath();
        vignettePath.AddEllipse(-size.Width * 0.3f, -size.Height * 0.3f, size.Width * 1.6f, size.Height * 1.6f);
        using var vignetteBrush = new PathGradientBrush(vignettePath)
        {
            CenterColor = Color.Transparent,
            SurroundColors = new[] { Color.FromArgb(140, 0, 0, 0) },
            CenterPoint = new PointF(size.Width / 2f, size.Height / 2f),
        };
        g.FillRectangle(vignetteBrush, 0, 0, size.Width, size.Height);

        _retroOverlayBitmap = bitmap;
    }

    private const string MenuReferenceText =
        "FILE\r\n" +
        "\r\n" +
        "  Open ROM...  (Ctrl+O)\r\n" +
        "    Opens a file picker to load a Genesis ROM (.bin/.md/.gen).\r\n" +
        "    Replaces whatever is currently running.\r\n" +
        "\r\n" +
        "  Save State...  (Ctrl+S)\r\n" +
        "    Saves a snapshot of the emulator's current state -- both\r\n" +
        "    CPUs, the video chip, the sound chips, and RAM -- to a\r\n" +
        "    .gss file. Defaults to a name based on the loaded ROM,\r\n" +
        "    saved next to it.\r\n" +
        "\r\n" +
        "  Load State...  (Ctrl+L)\r\n" +
        "    Loads a previously saved .gss file. If it was saved against\r\n" +
        "    a different ROM than the one currently loaded, you'll be\r\n" +
        "    asked to confirm before loading it anyway.\r\n" +
        "\r\n" +
        "  Exit\r\n" +
        "    Closes GenesisSharp.\r\n" +
        "\r\n" +
        "DEBUG\r\n" +
        "\r\n" +
        "  Show Debug Window  (Ctrl+D)\r\n" +
        "    Opens the debug window: live CPU/Z80 registers, a\r\n" +
        "    disassembly listing for each CPU, and a VRAM tile viewer.\r\n" +
        "    See that window's own Help menu for details.\r\n" +
        "\r\n" +
        "  Pause / Resume  (F5)\r\n" +
        "    Pauses or resumes emulation. While paused, the screen and\r\n" +
        "    audio both freeze in place.\r\n" +
        "\r\n" +
        "  Step Frame  (F6)\r\n" +
        "    Only does anything while paused. Advances exactly one\r\n" +
        "    video frame -- the CPU, sound chips, and video hardware\r\n" +
        "    all move forward together.\r\n" +
        "\r\n" +
        "  Step Instruction  (F7)\r\n" +
        "    Only does anything while paused. Executes a single 68000\r\n" +
        "    instruction. The Z80 and video hardware do not advance --\r\n" +
        "    this is for inspecting exactly where the main CPU is\r\n" +
        "    mid-frame, not a full-system step.\r\n" +
        "\r\n" +
        "DISPLAY\r\n" +
        "\r\n" +
        "  Retro\r\n" +
        "    Toggles a scanline/vignette overlay on the display, for a\r\n" +
        "    more CRT-like look. \"[X]\" means it's currently on. Purely\r\n" +
        "    cosmetic -- doesn't affect emulation.\r\n" +
        "\r\n" +
        "GAMEPLAY CONTROLS (Player 1)\r\n" +
        "\r\n" +
        "  Arrow keys   D-pad\r\n" +
        "  Z / X / C    A / B / C buttons\r\n" +
        "  Enter        Start\r\n";

    /// <summary>Opens a file picker and, if the user selects something, swaps in a fresh
    /// console for it. The previous console (if any) keeps running in the background on its own
    /// emulation thread while the modal dialog is up -- harmless now that emulation isn't tied
    /// to the UI thread the dialog blocks; <see cref="StartConsole"/> tears it down cleanly if
    /// a new ROM is actually chosen.</summary>
    private void PromptAndLoadRom()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Genesis ROMs (*.bin;*.md;*.gen)|*.bin;*.md;*.gen|All files (*.*)|*.*",
            InitialDirectory = _loadedRomPath is not null ? Path.GetDirectoryName(_loadedRomPath) : null,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadRomFromPath(dialog.FileName);
        }
    }

    private const string SaveStateFilter = "GenesisSharp save states (*.gss)|*.gss|All files (*.*)|*.*";

    /// <summary>Defaults to "<romname>.gss" next to the ROM (or its own directory if a save was
    /// already made there) -- the common "one obvious file per game" convention, while still
    /// letting the user pick a different name/location for multiple save slots.</summary>
    private void PromptAndSaveState()
    {
        if (_console is null)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = SaveStateFilter,
            DefaultExt = "gss",
            FileName = _loadedRomPath is not null ? Path.GetFileNameWithoutExtension(_loadedRomPath) + ".gss" : "state.gss",
            InitialDirectory = _loadedRomPath is not null ? Path.GetDirectoryName(_loadedRomPath) : null,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            PerformStateOperation(isSave: true, dialog.FileName, allowRomMismatch: false);
        }
    }

    private void PromptAndLoadState()
    {
        if (_console is null)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Filter = SaveStateFilter,
            InitialDirectory = _loadedRomPath is not null ? Path.GetDirectoryName(_loadedRomPath) : null,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            PerformStateOperation(isSave: false, dialog.FileName, allowRomMismatch: false);
        }
    }

    /// <summary>Hands a save/load request off to the emulation thread (see <see
    /// cref="_stateRequestPending"/>'s remarks) and blocks this (UI) thread briefly waiting for
    /// it to finish -- both operations are memory-copy-scale plus one small file, so this is
    /// normally sub-frame-length; the bounded wait below is a backstop against a stuck emulation
    /// thread, not an expected outcome. On a ROM-fingerprint mismatch, asks the user whether to
    /// load anyway rather than just failing outright.</summary>
    private void PerformStateOperation(bool isSave, string path, bool allowRomMismatch)
    {
        _stateRequestIsSave = isSave;
        _stateRequestPath = path;
        _stateRequestAllowRomMismatch = allowRomMismatch;
        _stateRequestError = null;
        _stateRequestDone = false;
        _stateRequestPending = true;

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_stateRequestDone && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(2);
        }

        if (!_stateRequestDone)
        {
            RetroMessageBox.Show(this, "Timed out waiting for the emulation thread to respond.", "GenesisSharp",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_stateRequestError is SaveStateRomMismatchException && !allowRomMismatch)
        {
            var result = RetroMessageBox.Show(this,
                "This save state was made with a different ROM than the one currently loaded. Load it anyway?",
                "GenesisSharp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                PerformStateOperation(isSave, path, allowRomMismatch: true);
            }

            return;
        }

        if (_stateRequestError is not null)
        {
            RetroMessageBox.Show(this, $"{(isSave ? "Save" : "Load")} state failed:\n{_stateRequestError.Message}", "GenesisSharp",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Runs on the emulation thread only, between whole frames -- see
    /// <see cref="_stateRequestPending"/>'s remarks. Clears <see cref="_stepFrameRequested"/>/
    /// <see cref="_stepInstructionRequested"/> too: those are meant for a single explicit step
    /// while paused, and a state load in particular changes so much underlying data that
    /// carrying a leftover step request into the *new* state would be surprising, not useful.</summary>
    private void PerformPendingStateRequestOnEmulationThread()
    {
        try
        {
            if (_stateRequestIsSave)
            {
                using var fileStream = File.Create(_stateRequestPath!);
                _console!.SaveState(fileStream);
            }
            else
            {
                using var fileStream = File.OpenRead(_stateRequestPath!);
                _console!.LoadState(fileStream, _stateRequestAllowRomMismatch);
            }

            _stateRequestError = null;
        }
        catch (Exception ex)
        {
            _stateRequestError = ex;
        }
        finally
        {
            _stepFrameRequested = false;
            _stepInstructionRequested = false;
            _stateRequestPending = false;
            _stateRequestDone = true;
        }
    }

    private void LoadRomFromPath(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            RetroMessageBox.Show(this, $"Couldn't read \"{path}\":\n{ex.Message}", "GenesisSharp",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        StartConsole(Cartridge.LoadFromBin(bytes), path);
    }

    /// <summary>(Re)initializes emulation state for a newly loaded cartridge — tears down
    /// whatever console/audio player/emulation thread was previously running (if any), builds a
    /// fresh console, and starts a fresh emulation thread for it. Shared by the constructor's
    /// initial-ROM-on-command-line path and the "Open ROM" menu item.</summary>
    private void StartConsole(Cartridge cartridge, string? romPath)
    {
        // Signal the old thread (if any) to stop and wait for it to actually exit before
        // touching _console again -- otherwise it could still be mid-RunFrame against the
        // *old* console when the lines below start reassigning fields it reads.
        _emulationThreadRunning = false;
        _emulationThread?.Join();

        _audioPlayer?.Stop();
        _audioPlayer?.Dispose();

        _console = new GenesisConsole(cartridge);
        _console.Reset();
        if (_versionRegisterOverride is byte versionOverride)
        {
            _console.VersionRegisterValue = versionOverride;
        }

        _visitedPcs.Clear();
        _ticksSinceNewPc = 0;
        _lockupDumped = false;
        _console.Cpu.InstructionFetching += pc => _visitedPcs.Add(pc);
        _console.Cpu.InstructionFetching += pc =>
        {
            if (_breakpointArmed && _breakpointAddress == pc)
            {
                throw new BreakpointHitException();
            }
        };

        _errorMessage = null;
        _loadedRomPath = romPath;
        // "[32X]" is purely a display indicator (Cartridge.Is32X's own remarks) -- it doesn't
        // change how the ROM is loaded or how Sega32X is wired in, which happens unconditionally
        // either way.
        string x32Suffix = cartridge.Is32X ? " [32X]" : "";
        _baseTitle = romPath is null ? "GenesisSharp" : $"GenesisSharp — {Path.GetFileName(romPath)}{x32Suffix}";
        Text = _baseTitle;

        // WASAPI's own internal buffer stays this many ms of audio queued ahead at all times --
        // a direct, constant contributor to the delay between a game action and hearing its
        // sound (originally 100ms; 50ms was still noticeably laggy per real-ROM playtesting, so
        // lowered further). 20ms is close to shared-mode WASAPI's practical floor -- Windows'
        // shared-mode audio engine period is itself typically ~10ms, so requesting much less than
        // that here wouldn't actually buy any additional reduction, just more underrun risk.
        // Lowered rather than removed: too small risks reintroducing the underruns/crackle that
        // moving emulation onto its own dedicated thread was originally meant to fix (see this
        // type's remarks) if the emulation thread's own scheduling jitter ever outpaces it.
        _audioPlayer = new WasapiOut(AudioClientShareMode.Shared, latency: 20);
        _audioPlayer.Init(new GenesisAudioProvider(_console));
        _audioPlayer.Play();

        _paused = false;
        _stepFrameRequested = false;
        _stepInstructionRequested = false;
        _pauseMenuItem.Text = "&Pause";
        _debugForm?.SetConsole(_console);

        _emulationThreadRunning = true;
        _emulationThread = new Thread(RunEmulationLoop) { IsBackground = true, Name = "GenesisSharp Emulation" };
        _emulationThread.Start();
    }

    private void ShowDebugWindow()
    {
        if (_debugForm is null)
        {
            _debugForm = new DebugForm(TogglePause, StepFrame, StepInstruction, SetBreakpoint, SetVersionRegisterOverride);
            _debugForm.SetConsole(_console);
        }

        _debugForm.Show();
        _debugForm.RefreshSnapshot();
    }

    private void TogglePause()
    {
        if (_console is null)
        {
            return;
        }

        _paused = !_paused;
        _pauseMenuItem.Text = _paused ? "&Resume" : "&Pause";
        _debugForm?.SetPausedLabel(_paused);
    }

    /// <summary>Requests exactly one video frame while paused -- the natural "step" granularity
    /// for a whole-system emulator, since sub-frame execution otherwise only advances the 68000
    /// (see <see cref="StepInstruction"/>). Just raises a flag the emulation thread's own loop
    /// (<see cref="RunEmulationLoop"/>) checks and acts on -- <see cref="GenesisConsole.RunFrame"/>
    /// must only ever run on that thread, never concurrently from here too.</summary>
    private void StepFrame()
    {
        if (_console is null)
        {
            return;
        }

        _stepFrameRequested = true;
    }

    /// <summary>Requests a single 68000 instruction step -- the Z80 and VDP don't advance, so
    /// this is for inspecting exactly where the 68000 is mid-frame, not a full-system single
    /// step. Deliberately simple for a first cut at this feature; a true whole-system
    /// instruction step would need <see cref="GenesisConsole"/> to expose sub-scanline
    /// stepping. See <see cref="StepFrame"/>'s remarks on why this only raises a flag.</summary>
    private void StepInstruction()
    {
        if (_console is null)
        {
            return;
        }

        _stepInstructionRequested = true;
    }

    /// <summary>Arms or disarms the 68000 PC breakpoint (see <see cref="_breakpointAddress"/>'s
    /// remarks) from the debug window's breakpoint row. Safe to call from the UI thread while
    /// the emulation thread is running -- reads/writes are plain volatile fields. Address is set
    /// before the armed flag so the emulation thread never observes "armed" with a stale
    /// (previous) address.</summary>
    private void SetBreakpoint(uint? address)
    {
        if (address is uint value)
        {
            _breakpointAddress = value;
            _breakpointArmed = true;
        }
        else
        {
            _breakpointArmed = false;
        }
    }

    /// <summary>Sets both the stored override (so it survives the next reload -- see
    /// <see cref="_versionRegisterOverride"/>'s remarks) and the live console's value (so it
    /// takes effect immediately for the currently-running instance too, without needing a
    /// reload first).</summary>
    private void SetVersionRegisterOverride(byte value)
    {
        _versionRegisterOverride = value;
        if (_console is not null)
        {
            _console.VersionRegisterValue = value;
        }
    }

    /// <summary>The emulation thread's main loop: a fixed-timestep runner, paced against a
    /// <see cref="Stopwatch"/> rather than a WinForms timer (see the type remarks), that catches
    /// up with a bounded burst of frames rather than permanently drifting if something (a
    /// debugger break, a long GC pause, the machine sleeping) delays it. Runs until <see
    /// cref="_emulationThreadRunning"/> is cleared, which <see cref="StartConsole"/> and <see
    /// cref="Dispose"/> both do before tearing down anything this thread touches.</summary>
    private void RunEmulationLoop()
    {
        const double frameSeconds = 1.0 / 60.0; // pacing only -- chip/CPU timing is cycle-based and unaffected by this
        const double maxCatchUpSeconds = 0.25; // cap how big a burst a long stall can trigger

        var stopwatch = Stopwatch.StartNew();
        long lastTicks = stopwatch.ElapsedTicks;
        double accumulatedSeconds = 0;

        while (_emulationThreadRunning)
        {
            if (_stateRequestPending)
            {
                PerformPendingStateRequestOnEmulationThread();
                lastTicks = stopwatch.ElapsedTicks;
                accumulatedSeconds = 0;
                continue;
            }

            if (_paused)
            {
                if (_stepFrameRequested)
                {
                    _stepFrameRequested = false;
                    RunOneFrame();
                }
                else if (_stepInstructionRequested)
                {
                    _stepInstructionRequested = false;
                    RunOneInstruction();
                }
                else
                {
                    Thread.Sleep(5);
                }

                lastTicks = stopwatch.ElapsedTicks;
                accumulatedSeconds = 0;
                _framesSkippedLastBurst = 0; // being paused isn't being behind
                continue;
            }

            long nowTicks = stopwatch.ElapsedTicks;
            accumulatedSeconds += (nowTicks - lastTicks) / (double)Stopwatch.Frequency;
            lastTicks = nowTicks;
            accumulatedSeconds = Math.Min(accumulatedSeconds, maxCatchUpSeconds);

            // Frame-skip: run every frame the clock says is due, but present only the last one.
            // Emulation is untouched -- this drops *rendering* work, never emulated frames, so
            // behavior stays identical and only what reaches the screen changes. Worth doing
            // because presenting is not free: UpdateFrameSnapshot copies the whole 320x224 RGB24
            // buffer, and during a catch-up burst every copy but the final one is overwritten
            // before the UI thread ever looks at it, so the burst was paying for work nobody could
            // see -- exactly when there was least headroom to spare.
            int framesRun = 0;
            while (accumulatedSeconds >= frameSeconds && _emulationThreadRunning && !_paused)
            {
                RunOneFrame(present: false);
                accumulatedSeconds -= frameSeconds;
                framesRun++;
            }

            if (framesRun > 0)
            {
                UpdateFrameSnapshot();
                _framesSkippedLastBurst = framesRun - 1; // diagnostic only; 0 when keeping up
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>Runs exactly one video frame -- the emulation thread's per-iteration unit of
    /// work, and also what a paused <see cref="StepFrame"/> request performs. Stops the
    /// emulation thread's own loop (by clearing <see cref="_emulationThreadRunning"/>) on error
    /// rather than the UI thread's timer, since this now runs there instead.</summary>
    /// <param name="present">Whether to publish the finished frame for the UI thread. False during
    /// a catch-up burst for every frame but the last — see <see cref="RunEmulationLoop"/>. The
    /// paused step-frame path passes true, since there the whole point is to see that one frame.</param>
    private void RunOneFrame(bool present = true)
    {
        int pcCountBefore = _visitedPcs.Count;
        try
        {
            _console!.RunFrame();
        }
        catch (BreakpointHitException)
        {
            // Not a real error -- see _breakpointAddress's remarks. Pause exactly where the
            // CPU was about to fetch the breakpoint's opcode; UI-thread state (menu text, debug
            // form label) is synced from _breakpointHit by RepaintAndRefreshDebugWindow, never
            // touched directly from this thread.
            _paused = true;
            _breakpointHit = true;
            return;
        }
        catch (Exception ex)
        {
            // Plenty of real cartridges will reach code paths this emulator doesn't support
            // yet (bank-switching mappers, battery SRAM, TMSS-gated logic, and so on) --
            // surface that instead of taking down this thread's caller silently.
            _errorMessage = $"Emulation stopped: {ex.Message}";
            _audioPlayer?.Stop();
            _emulationThreadRunning = false;
            return;
        }

        _ticksSinceNewPc = _visitedPcs.Count > pcCountBefore ? 0 : _ticksSinceNewPc + 1;
        if (_ticksSinceNewPc >= 180 && !_lockupDumped)
        {
            _lockupDumped = true;
            DumpLockupDiagnostics();
        }

        if (present)
        {
            UpdateFrameSnapshot();
        }
    }

    /// <summary>Runs a single 68000 instruction -- see <see cref="StepInstruction"/>'s remarks
    /// on why this is CPU-only, not a whole-system step.</summary>
    private void RunOneInstruction()
    {
        try
        {
            _console!.Cpu.Step();
        }
        catch (BreakpointHitException)
        {
            // See RunOneFrame's identical catch -- not a real error. A single step can hit the
            // armed breakpoint just as validly as a full frame can (e.g. it's still armed from
            // an earlier test at a not-yet-reached address); stay paused rather than crash the
            // emulation thread.
            _paused = true;
            _breakpointHit = true;
            return;
        }
        catch (Exception ex)
        {
            _errorMessage = $"Emulation stopped: {ex.Message}";
            _audioPlayer?.Stop();
            _emulationThreadRunning = false;
            return;
        }

        UpdateFrameSnapshot();
    }

    /// <summary>Copies the just-finished frame into <see cref="_frameSnapshot"/> under its lock
    /// -- called only from the emulation thread, right after a frame/instruction completes and
    /// before this same thread starts overwriting <see cref="Vdp.FrameBuffer"/> again, so the
    /// copy's source is never concurrently written while this runs.</summary>
    private void UpdateFrameSnapshot()
    {
        lock (_frameSnapshotLock)
        {
            Array.Copy(_console!.Vdp.FrameBuffer, _frameSnapshot, _frameSnapshot.Length);
        }
    }

    /// <summary>The UI thread's timer tick: paints whatever frame the emulation thread most
    /// recently finished and refreshes the debug window, if open. Purely presentational now --
    /// see the type remarks for why this no longer runs emulation itself.</summary>
    private void RepaintAndRefreshDebugWindow()
    {
        if (_console is not null)
        {
            CopyFrameBufferToBitmap();
        }

        _display.Invalidate();
        _debugForm?.RefreshSnapshot();

        // Surface a speed deficit rather than leaving it to be inferred from how the game feels or
        // sounds. Without this, "running at a third speed" and "the sound chip is broken" present
        // identically -- which is exactly how a Debug-vs-Release build once cost a long detour into
        // the PWM implementation (see ARCHITECTURE.md §9.8). Only shown while actually behind.
        int framesBehind = _framesSkippedLastBurst;
        string desiredTitle = framesBehind > 0 ? $"{_baseTitle} — behind {framesBehind}f" : _baseTitle;
        if (Text != desiredTitle)
        {
            Text = desiredTitle;
        }

        if (_breakpointHit)
        {
            _breakpointHit = false;
            _pauseMenuItem.Text = "&Resume";
            _debugForm?.SetPausedLabel(true);
        }
    }

    /// <summary>Temporary lockup-diagnosis hook — writes CPU/VDP/controller state to a file
    /// next to the executable the moment a likely lockup is detected (see <see
    /// cref="_visitedPcs"/>), so a freeze that only reproduces via real keyboard input during
    /// interactive play can still be inspected afterward.</summary>
    private void DumpLockupDiagnostics()
    {
        var cpu = _console!.Cpu;
        var vdp = _console.Vdp;
        var pad = _console.ControllerPort1.Pad;
        string path = Path.Combine(AppContext.BaseDirectory, "lockup_diagnostics.txt");

        var lines = new List<string>
        {
            $"Lockup detected at {DateTime.Now:O} -- no new PC visited for {_ticksSinceNewPc} ticks.",
            $"Total distinct PCs visited so far: {_visitedPcs.Count}",
            $"PC={cpu.PC:X6} SR={cpu.SR:X4} TotalCycles={cpu.TotalCycles}",
        };
        for (int i = 0; i < 8; i++) lines.Add($"D{i}={cpu.D[i]:X8}");
        for (int i = 0; i < 8; i++) lines.Add($"A{i}={cpu.A[i]:X8}");
        lines.Add($"Pad: Up={pad.Up} Down={pad.Down} Left={pad.Left} Right={pad.Right} A={pad.A} B={pad.B} C={pad.C} Start={pad.Start}");
        lines.Add($"VDP: Registers1={vdp.Registers[1]:X2} DisplayEnabled={vdp.DisplayEnabled} CurrentScanline={vdp.CurrentScanline}");
        lines.Add("Last 200 distinct PCs visited (unordered set, for reference):");
        lines.AddRange(_visitedPcs.OrderBy(x => x).Select(x => $"  {x:X6}"));

        File.WriteAllLines(path, lines);
    }

    private void OnDisplayPaint(object? sender, PaintEventArgs e)
    {
        // A WM_PAINT can still be queued and delivered after Dispose(true) has already torn
        // down _frameBitmap (e.g. while the form is closing) -- drawing into/with a disposed
        // GDI+ object throws a bare ArgumentException ("Parameter is not valid"), not
        // ObjectDisposedException, so it isn't something a narrower catch would name usefully.
        if (_isClosing)
        {
            return;
        }

        if (_errorMessage is not null)
        {
            e.Graphics.DrawString(_errorMessage, Font, Brushes.Red, new RectangleF(10, 10, _display.Width - 20, _display.Height - 20));
            return;
        }

        if (_console is null)
        {
            e.Graphics.DrawString("File > Open ROM... to begin.", Font, Brushes.White, 10, 10);
            return;
        }

        e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        e.Graphics.DrawImage(_frameBitmap, _display.ClientRectangle);

        if (_retroOverlayEnabled)
        {
            Rectangle displayBounds = _display.ClientRectangle;
            if (displayBounds.Width > 0 && displayBounds.Height > 0)
            {
                EnsureRetroOverlayBitmap(displayBounds.Size);
                e.Graphics.DrawImage(_retroOverlayBitmap!, displayBounds);
            }
        }
    }

    /// <summary>Reads from <see cref="_frameSnapshot"/> (the emulation thread's latest
    /// completed-frame copy), not <see cref="Vdp.FrameBuffer"/> directly -- see the type
    /// remarks. Packed R,G,B per pixel there, but GDI+'s <see cref="PixelFormat.Format24bppRgb"/>
    /// (despite the name) stores bytes in B,G,R order in memory -- swapped here rather than
    /// changing the frame buffer's layout, which is exercised byte-for-byte by a few hundred
    /// existing VDP tests.</summary>
    private void CopyFrameBufferToBitmap()
    {
        var bmp = _frameBitmap;
        byte[] row = _bitmapRowBuffer;
        int srcRowStride = Vdp.ScreenWidth * 3;

        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            lock (_frameSnapshotLock)
            {
                byte[] source = _frameSnapshot;
                for (int y = 0; y < bmp.Height; y++)
                {
                    int srcOffset = y * srcRowStride;
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int s = srcOffset + x * 3;
                        int d = x * 3;
                        row[d] = source[s + 2];     // B
                        row[d + 1] = source[s + 1]; // G
                        row[d + 2] = source[s];     // R
                    }

                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>Player 1 only, for now. Arrow keys for the D-pad, Z/X/C for A/B/C (three
    /// buttons in a row, common across Genesis-emulator keyboard defaults), Enter for Start.</summary>
    private void SetButtonState(Keys key, bool pressed)
    {
        if (_console is null)
        {
            return;
        }

        GamePad pad = _console.ControllerPort1.Pad;
        switch (key)
        {
            case Keys.Up: pad.Up = pressed; break;
            case Keys.Down: pad.Down = pressed; break;
            case Keys.Left: pad.Left = pressed; break;
            case Keys.Right: pad.Right = pressed; break;
            case Keys.Z: pad.A = pressed; break;
            case Keys.X: pad.B = pressed; break;
            case Keys.C: pad.C = pressed; break;
            case Keys.Enter: pad.Start = pressed; break;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e) => SetButtonState(e.KeyCode, pressed: true);
    private void OnKeyUp(object? sender, KeyEventArgs e) => SetButtonState(e.KeyCode, pressed: false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _isClosing = true;
            _emulationThreadRunning = false;
            _emulationThread?.Join(1000); // bounded -- don't hang shutdown indefinitely if something's gone wrong
            _timer.Dispose();
            _frameBitmap.Dispose();
            _retroOverlayBitmap?.Dispose();
            _audioPlayer?.Stop();
            _audioPlayer?.Dispose();

            // DebugForm's own close handler just hides it (see its constructor) so it can be
            // cheaply reopened -- that means it wouldn't otherwise get torn down when the main
            // window does.
            _debugForm?.Dispose();
        }

        base.Dispose(disposing);
    }
}
