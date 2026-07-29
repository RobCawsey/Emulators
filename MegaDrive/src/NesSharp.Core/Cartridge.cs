namespace NesSharp.Core;

/// <summary>How the PPU's 2KB of physical VRAM maps onto the four logical nametable slots.
/// Horizontal/Vertical are fixed by the cartridge for simple mappers; SingleScreen* are
/// switchable at runtime by mappers like MMC1 that can bank-switch mirroring itself.</summary>
public enum MirroringMode
{
    Horizontal,
    Vertical,
    SingleScreenLower,
    SingleScreenUpper,
}

/// <summary>Parsed iNES (.nes) ROM data — header fields plus the raw PRG/CHR banks,
/// mapper-agnostic. <see cref="NesConsole"/> picks the matching <see cref="IMapper"/>
/// implementation for <see cref="MapperNumber"/>.</summary>
public sealed class Cartridge
{
    public byte[] Prg { get; }
    public byte[] Chr { get; }
    public byte MapperNumber { get; }
    public MirroringMode Mirroring { get; }
    public bool HasBattery { get; }

    private Cartridge(byte[] prg, byte[] chr, byte mapperNumber, MirroringMode mirroring, bool hasBattery)
    {
        Prg = prg;
        Chr = chr;
        MapperNumber = mapperNumber;
        Mirroring = mirroring;
        HasBattery = hasBattery;
    }

    public static Cartridge LoadFromInes(byte[] data)
    {
        if (data.Length < 16 || data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A)
        {
            throw new InvalidDataException("Not an iNES (.nes) file — missing the 'NES\\x1A' magic header.");
        }

        int prgBanks = data[4];
        int chrBanks = data[5];
        byte flags6 = data[6];
        byte flags7 = data[7];

        bool hasTrainer = (flags6 & 0x04) != 0;
        var mirroring = (flags6 & 0x01) != 0 ? MirroringMode.Vertical : MirroringMode.Horizontal;
        bool hasBattery = (flags6 & 0x02) != 0;
        byte mapperNumber = (byte)((flags7 & 0xF0) | (flags6 >> 4));

        int offset = 16 + (hasTrainer ? 512 : 0);

        int prgSize = prgBanks * 16 * 1024;
        var prg = new byte[prgSize];
        Array.Copy(data, offset, prg, 0, prgSize);
        offset += prgSize;

        int chrSize = chrBanks * 8 * 1024;
        var chr = new byte[chrSize]; // chrBanks == 0 means CHR-RAM; caller treats an empty array as such
        if (chrSize > 0)
        {
            Array.Copy(data, offset, chr, 0, chrSize);
        }

        return new Cartridge(prg, chr, mapperNumber, mirroring, hasBattery);
    }
}
