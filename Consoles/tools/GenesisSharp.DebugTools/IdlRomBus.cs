using GenesisSharp.CpuSh2;

namespace GenesisSharp.DebugTools;

/// <summary>Presents a 32X cartridge image at the SH-2 addresses its code actually runs from,
/// without emulating anything — enough for a disassembler, which only ever reads.
///
/// A 32X cartridge header carries an "Initial Data Load" descriptor: a source offset in ROM, a
/// destination in SDRAM, and a length. The 32X's boot code copies that block before handing over
/// to the game, so a game's SDRAM-resident code is a straight copy of a known ROM range and can be
/// disassembled straight out of the file. That's the whole trick this class implements, and it is
/// why <see cref="DisassembleCommand"/> needs no running emulator.
///
/// Two windows are mapped:
/// <list type="bullet">
/// <item>CS3 (<c>$06000000</c>) — SDRAM, resolved through the IDL descriptor above.</item>
/// <item>CS1 (<c>$02000000</c>) — the cartridge ROM itself, unbanked, read straight through.</item>
/// </list>
/// The cache-through bit (<c>$20000000</c>) is masked off first, so <c>$26001480</c> and
/// <c>$06001480</c> resolve identically, matching <c>Sega32XSh2Bus</c>'s own decode.
///
/// Caveat worth stating plainly: this is the ROM's *initial* image. Anything the game generates,
/// decompresses, or overwrites at runtime will not match, and code loaded by any later copy the
/// game performs itself is not visible here at all. For those, use the debug window's live peek
/// against a running console instead.</summary>
internal sealed class IdlRomBus : IBus
{
    private const uint CacheThroughBit = 0x2000_0000;
    private const uint Cs1Base = 0x0200_0000;
    private const uint Cs3Base = 0x0600_0000;
    private const uint SdramSize = 0x0004_0000;

    /// <summary>Offset of the Initial Data Load descriptor in a 32X cartridge header: source
    /// (4 bytes), destination (4), length (4). Verified against a real commercial dump by
    /// disassembling several SDRAM addresses through it and matching the result, instruction for
    /// instruction, against live memory dumps from the running debug window.</summary>
    private const int IdlHeaderOffset = 0x3D4;

    private readonly byte[] _rom;

    public IdlRomBus(byte[] rom)
    {
        _rom = rom;
        IdlSource = ReadRomLong(IdlHeaderOffset);
        IdlDestination = ReadRomLong(IdlHeaderOffset + 4);
        IdlLength = ReadRomLong(IdlHeaderOffset + 8);
    }

    public uint IdlSource { get; }

    public uint IdlDestination { get; }

    public uint IdlLength { get; }

    /// <summary>Maps an SH-2 address to an index into the ROM image, or -1 if this tool can't
    /// resolve it (an unmapped area, or an SDRAM address outside the IDL block).</summary>
    private int MapToRom(uint address)
    {
        uint decoded = address & ~CacheThroughBit;

        if (decoded >= Cs3Base && decoded < Cs3Base + SdramSize)
        {
            uint sdramOffset = decoded - Cs3Base;
            if (sdramOffset < IdlDestination || sdramOffset >= IdlDestination + IdlLength)
            {
                return -1; // outside what the IDL copy actually populates
            }

            long romOffset = IdlSource + (sdramOffset - IdlDestination);
            return romOffset < _rom.Length ? (int)romOffset : -1;
        }

        if (decoded >= Cs1Base && decoded < Cs3Base)
        {
            uint romOffset = decoded - Cs1Base;
            return romOffset < _rom.Length ? (int)romOffset : -1;
        }

        return -1;
    }

    public byte ReadByte(uint address)
    {
        int offset = MapToRom(address);
        return offset < 0 ? (byte)0 : _rom[offset];
    }

    public ushort ReadWord(uint address) => (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));

    public uint ReadLong(uint address) => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

    // Read-only by design: this backs a disassembler, and silently accepting writes would make a
    // mistyped command look like it worked.
    public void WriteByte(uint address, byte value) => throw new NotSupportedException("IdlRomBus is read-only.");

    public void WriteWord(uint address, ushort value) => throw new NotSupportedException("IdlRomBus is read-only.");

    public void WriteLong(uint address, uint value) => throw new NotSupportedException("IdlRomBus is read-only.");

    private uint ReadRomLong(int offset) =>
        ((uint)_rom[offset] << 24) | ((uint)_rom[offset + 1] << 16) |
        ((uint)_rom[offset + 2] << 8) | _rom[offset + 3];
}
