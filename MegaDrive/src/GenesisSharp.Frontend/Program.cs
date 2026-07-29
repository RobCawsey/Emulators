using GenesisSharp.Core;

namespace GenesisSharp.Frontend;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // A ROM path on the command line still loads immediately, for convenience (e.g. a
        // file-association double-click) -- otherwise the window opens empty and the user
        // picks one via File > Open ROM.
        string? romPath = args.Length > 0 ? args[0] : null;

        ApplicationConfiguration.Initialize();

        // Global, not per-form: a ToolStripDropDownMenu (what backs every MenuStrip dropdown)
        // reads ToolStripManager's renderer, not its owning MenuStrip's -- setting this once
        // here is what makes RetroMenuRenderer apply to dropdowns too, not just the top-level bar.
        ToolStripManager.Renderer = new RetroMenuRenderer();

        Application.Run(new MainForm(romPath is null ? null : Cartridge.LoadFromBin(File.ReadAllBytes(romPath)), romPath));
    }
}
