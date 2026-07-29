using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using NesSharp.Core;

namespace NesSharp.Frontend;

public sealed class MainForm : Form
{
    private const int PixelScale = 3;

    private readonly NesConsole _console;
    private readonly Bitmap _bitmap = new(256, 240, PixelFormat.Format32bppArgb);
    private readonly FrameBitmapRenderer _renderer = new();
    private readonly AudioPlayer _audio = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 16 }; // ~60Hz
    private NesButtons _buttons;

    public MainForm(NesConsole console)
    {
        _console = console;

        Text = "NesSharp";
        ClientSize = new Size(256 * PixelScale, 240 * PixelScale);
        DoubleBuffered = true;
        KeyPreview = true;

        _timer.Tick += (_, _) => RunOneFrameAndRedraw();
        _timer.Start();

        KeyDown += (_, e) => SetButton(e.KeyCode, pressed: true);
        KeyUp += (_, e) => SetButton(e.KeyCode, pressed: false);
    }

    private void RunOneFrameAndRedraw()
    {
        long targetFrame = _console.Ppu.FrameCount + 1;
        // Safety cap: a real frame is ~29,780 CPU cycles: bail out well past that instead of
        // freezing the UI thread if something is stuck (e.g. a mapper/game we don't support
        // yet spin-waiting on a PPU flag that never flips).
        long cycleBudget = _console.Cpu.TotalCycles + 200_000;
        while (_console.Ppu.FrameCount < targetFrame && _console.Cpu.TotalCycles < cycleBudget)
        {
            _console.Clock();
        }

        _renderer.Render(_console.Ppu.Frame, _bitmap);
        _audio.Enqueue(_console.Apu.SampleBuffer);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_bitmap, ClientRectangle);
    }

    private void SetButton(Keys key, bool pressed)
    {
        NesButtons button = key switch
        {
            Keys.Z => NesButtons.A,
            Keys.X => NesButtons.B,
            Keys.Space => NesButtons.Select,
            Keys.Enter => NesButtons.Start,
            Keys.Up => NesButtons.Up,
            Keys.Down => NesButtons.Down,
            Keys.Left => NesButtons.Left,
            Keys.Right => NesButtons.Right,
            _ => NesButtons.None,
        };
        if (button == NesButtons.None)
        {
            return;
        }
        _buttons = pressed ? _buttons | button : _buttons & ~button;
        _console.Bus.Controller1.SetButtons(_buttons);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _audio.Dispose();
        base.OnFormClosed(e);
    }
}
