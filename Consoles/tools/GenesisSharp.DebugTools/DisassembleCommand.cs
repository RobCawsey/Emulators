using GenesisSharp.Frontend; // Sh2Disassembler, link-compiled -- see the .csproj's own remarks.

namespace GenesisSharp.DebugTools;

/// <summary>Disassembles SH-2 code straight out of a 32X cartridge image, with no emulator
/// running. Useful when you need to read a whole routine at once: the debug window's live peek
/// shows a screenful at a time and needs the game paused at the right moment, whereas this will
/// dump an arbitrary range in one go and can be diffed, searched, and pasted around.</summary>
internal static class DisassembleCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: disasm <rom> <sh2-address-hex> [instruction-count]");
            return 2;
        }

        string romPath = args[0];
        if (!TryParseHex(args[1], out uint start))
        {
            Console.Error.WriteLine($"Not a hex address: {args[1]}");
            return 2;
        }

        int count = 32;
        if (args.Length > 2 && !int.TryParse(args[2], out count))
        {
            Console.Error.WriteLine($"Not an instruction count: {args[2]}");
            return 2;
        }

        var bus = new IdlRomBus(File.ReadAllBytes(romPath));
        Console.WriteLine($"; {Path.GetFileName(romPath)}");
        Console.WriteLine($"; IDL: src=0x{bus.IdlSource:X8} dst=0x{bus.IdlDestination:X8} len=0x{bus.IdlLength:X8}");
        Console.WriteLine();

        foreach (var instruction in Sh2Disassembler.DisassembleRange(start, count, bus))
        {
            Console.WriteLine($"{instruction.Address:X8}  {bus.ReadWord(instruction.Address):X4}  {instruction.Text}");
        }

        return 0;
    }

    private static bool TryParseHex(string text, out uint value) =>
        uint.TryParse(text.TrimStart('$').Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                      System.Globalization.NumberStyles.HexNumber, null, out value);
}
