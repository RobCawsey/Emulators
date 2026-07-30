namespace GenesisSharp.Cpu68000;

/// <summary>The 68000's view of the outside world: work RAM, cartridge ROM/RAM, VDP ports,
/// the Z80 bank window, and I/O — wired up by GenesisSharp.Core. Widths follow 68000
/// terminology (byte/word/long = 8/16/32 bits).</summary>
public interface IBus
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadLong(uint address);

    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteLong(uint address, uint value);
}
