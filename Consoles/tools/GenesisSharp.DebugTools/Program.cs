using GenesisSharp.DebugTools;

// Command-line companions to the debug window, for the questions it answers badly: reading a whole
// SH-2 routine at once, and watching 32X display state across hundreds of frames. Both were built
// while root-causing real bugs in a commercial 32X title and are kept because the next such
// investigation will want them again -- see ARCHITECTURE.md §9.

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

string command = args[0];
string[] rest = args[1..];

return command switch
{
    "disasm" => DisassembleCommand.Run(rest),
    "probe" => FrameBufferProbeCommand.Run(rest),
    "-h" or "--help" or "help" => PrintUsage(),
    _ => UnknownCommand(command),
};

static int PrintUsage()
{
    Console.WriteLine("""
        GenesisSharp debug tools.

          disasm <rom> <sh2-address-hex> [count]
              Disassemble SH-2 code out of a 32X cartridge image, no emulator running.
              SDRAM addresses ($06xxxxxx) resolve through the cartridge's Initial Data
              Load descriptor; cartridge addresses ($02xxxxxx) read straight through.
              Shows the ROM's initial image only -- anything the game decompresses or
              overwrites at runtime needs the debug window's live peek instead.

          probe <rom> [frames]
              Run a ROM headlessly and report 32X display state per frame, printing only
              on change: FS, display mode, register ownership, and how many scanlines
              each frame-buffer bank's line table can actually resolve.

        Examples:
          disasm "SagaRoms/Pitfall - The Mayan Adventure (USA).32x" 060013F4 64
          probe  "SagaRoms/Pitfall - The Mayan Adventure (USA).32x" 900
        """);
    return 0;
}

static int UnknownCommand(string command)
{
    Console.Error.WriteLine($"Unknown command '{command}'. Try --help.");
    return 2;
}
