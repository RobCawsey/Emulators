namespace GenesisSharp.Core;

/// <summary>A loaded Genesis/Mega Drive cartridge image. Header parsing and bank-switching
/// mappers (e.g. SSF2) are not implemented yet — this is scaffolding for a later milestone.
/// <see cref="Is32X"/> is the one exception: a minimal, display-only header check (Phase 5 of the
/// in-progress 32X extension, see ARCHITECTURE.md §4a.4), not general header parsing.</summary>
public sealed class Cartridge
{
    private const int HeaderConsoleNameOffset = 0x100;
    private const int HeaderConsoleNameLength = 16;

    public byte[] Rom { get; }

    /// <summary>True if the standard Genesis header's console-name field (16 ASCII bytes at ROM
    /// offset 0x100, the same field real hardware/emulators read to show e.g. "SEGA GENESIS")
    /// contains "SEGA 32X" — the documented convention real 32X titles use to identify themselves
    /// to the 32X's own boot ROM. UI-indicator-only: nothing in <see cref="GenesisConsole"/> or
    /// <see cref="Sega32X"/> reads this — the 32X subsystem is always wired in and only ever
    /// activated by a ROM's own code setting ADEN/nRES itself (see <see cref="Sega32X"/>'s own
    /// remarks), exactly like before this property existed.</summary>
    public bool Is32X { get; }

    private Cartridge(byte[] rom)
    {
        Rom = rom;
        Is32X = DetectSega32XHeader(rom);
    }

    private static bool DetectSega32XHeader(byte[] rom)
    {
        if (rom.Length < HeaderConsoleNameOffset + HeaderConsoleNameLength)
        {
            return false;
        }

        string consoleName = System.Text.Encoding.ASCII.GetString(rom, HeaderConsoleNameOffset, HeaderConsoleNameLength);
        return consoleName.Contains("SEGA 32X", StringComparison.Ordinal);
    }

    /// <summary>Loads a raw headerless ROM image (.bin/.md/.gen).</summary>
    public static Cartridge LoadFromBin(byte[] rom) => new(rom);
}
