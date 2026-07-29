namespace GenesisSharp.Core;

/// <summary>A loaded Genesis/Mega Drive cartridge image. Header parsing and bank-switching
/// mappers (e.g. SSF2) are not implemented yet — this is scaffolding for a later milestone.</summary>
public sealed class Cartridge
{
    public byte[] Rom { get; }

    private Cartridge(byte[] rom)
    {
        Rom = rom;
    }

    /// <summary>Loads a raw headerless ROM image (.bin/.md/.gen).</summary>
    public static Cartridge LoadFromBin(byte[] rom) => new(rom);
}
