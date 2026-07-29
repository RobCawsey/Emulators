using System.Drawing.Imaging;
using NesSharp.Core;

namespace NesSharp.Frontend;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--shot")
        {
            RunHeadlessScreenshot(romPath: args[1], outputPngPath: args[2], frameCount: int.Parse(args[3]));
            return;
        }

        string? romPath = args.Length > 0 ? args[0] : PromptForRomPath();
        if (romPath is null)
        {
            return;
        }

        var console = new NesConsole(Cartridge.LoadFromInes(File.ReadAllBytes(romPath)));
        console.Reset();

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(console));
    }

    private static string? PromptForRomPath()
    {
        using var dialog = new OpenFileDialog { Filter = "NES ROMs (*.nes)|*.nes|All files (*.*)|*.*" };
        return dialog.ShowDialog() == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>Runs a ROM headlessly for a fixed number of frames and saves the last one as
    /// a PNG — used to visually verify rendering without needing an interactive session.</summary>
    private static void RunHeadlessScreenshot(string romPath, string outputPngPath, int frameCount)
    {
        var console = new NesConsole(Cartridge.LoadFromInes(File.ReadAllBytes(romPath)));
        console.Reset();

        long targetFrame = console.Ppu.FrameCount + frameCount;
        long cycleBudget = console.Cpu.TotalCycles + 200_000L * frameCount;
        while (console.Ppu.FrameCount < targetFrame && console.Cpu.TotalCycles < cycleBudget)
        {
            console.Clock();
        }

        using var bitmap = new Bitmap(256, 240, PixelFormat.Format32bppArgb);
        new FrameBitmapRenderer().Render(console.Ppu.Frame, bitmap);
        bitmap.Save(outputPngPath, ImageFormat.Png);

        Console.WriteLine($"Reached frame {console.Ppu.FrameCount} after {console.Cpu.TotalCycles} CPU cycles; saved {outputPngPath}.");
    }
}
