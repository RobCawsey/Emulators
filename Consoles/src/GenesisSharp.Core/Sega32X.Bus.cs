using GenesisSharp.CpuSh2;

namespace GenesisSharp.Core;

/// <summary>One SH-2's view of the 32X bus — constructed twice by <see cref="Sega32X"/>, once per
/// core, distinguished only by <see cref="_isSlave"/> (which boot ROM it reads; no other
/// asymmetry is modeled yet). CS0-CS3 area decode confirmed against PicoDrive's
/// <c>pico/32x/memory.c</c> (dispatch keyed on <c>address &gt;&gt; SH2_READ_SHIFT</c>,
/// <c>SH2_READ_SHIFT = 25</c>, <c>pico/pico_int.h:639-640</c>): CS0 (boot ROM + adapter sysregs)
/// at $00000000, CS1 (cartridge ROM) at $02000000, CS2 (frame buffer) at $04000000, CS3 (SDRAM) at
/// $06000000, each mirrored again with bit 0x20000000 set for the "cache-through" view — this core
/// has no cache model, so both views are handled identically.
///
/// Unlike PicoDrive's own bit-masked sub-decode (which mirrors these register blocks across a
/// wider address range because real hardware only decodes a partial set of address bits), this
/// only recognizes the *canonical* windows for them — real software addresses them canonically,
/// so this is a deliberate simplification, not a guess.</summary>
internal sealed class Sega32XSh2Bus : IBus
{
    private const uint Cs0Base = 0x0000_0000;
    private const uint Cs1Base = 0x0200_0000;
    private const uint Cs2Base = 0x0400_0000;
    private const uint Cs3Base = 0x0600_0000;
    private const uint CsSize = 0x0200_0000;
    private const uint CacheThroughBit = 0x2000_0000;

    private const uint AdapterRegLow = 0x4000;
    private const uint AdapterRegHigh = 0x403F;
    private const uint VdpRegLow = 0x4100;
    private const uint VdpRegHigh = 0x411F;
    private const uint PaletteLow = 0x4200;
    private const uint PaletteHigh = 0x43FF;

    /// <summary>Within CS2, this bit selects the "overwrite" (masked, zero-bytes-pass-through)
    /// write window over the plain direct one — confirmed in Phase 2's own research pass against
    /// <c>sh2_write8_dramN</c>/<c>sh2_write16_dramN</c> (<c>memory.c</c>).</summary>
    private const uint FrameBufferOverwriteBit = 0x0002_0000;

    private readonly Sega32X _console32X;
    private readonly bool _isSlave;

    public Sega32XSh2Bus(Sega32X console32X, bool isSlave)
    {
        _console32X = console32X;
        _isSlave = isSlave;
    }

    public byte ReadByte(uint address)
    {
        uint decoded = address & ~CacheThroughBit;

        if (decoded < Cs1Base) // CS0: boot ROM + adapter sysregs + vdp_regs + palette
        {
            if (decoded is >= AdapterRegLow and <= AdapterRegHigh)
            {
                return _console32X.ReadControlByteForSh2(decoded - AdapterRegLow);
            }

            if (decoded is >= VdpRegLow and <= VdpRegHigh)
            {
                return _console32X.ReadVdpControlByteForSh2(decoded - VdpRegLow);
            }

            if (decoded is >= PaletteLow and <= PaletteHigh)
            {
                return _console32X.ReadPaletteByteForSh2(decoded - PaletteLow);
            }

            byte[] bootRom = _isSlave ? _console32X.BootRomSlave : _console32X.BootRomMaster;
            return decoded < (uint)bootRom.Length ? bootRom[decoded] : (byte)0;
        }

        if (decoded is >= Cs1Base and < Cs2Base) // CS1: cartridge ROM, unbanked
        {
            byte[] rom = _console32X.CartridgeRom;
            return rom.Length > 0 ? rom[(decoded - Cs1Base) % (uint)rom.Length] : (byte)0xFF;
        }

        if (decoded is >= Cs2Base and < Cs2Base + CsSize) // CS2: frame buffer
        {
            return _console32X.ReadFrameBufferByteForSh2((decoded - Cs2Base) & 0x1FFFF);
        }

        if (decoded is >= Cs3Base and < Cs3Base + CsSize) // CS3: SDRAM
        {
            return _console32X.Sdram[(decoded - Cs3Base) & 0x0003_FFFF];
        }

        return 0;
    }

    public ushort ReadWord(uint address)
    {
        uint decoded = address & ~CacheThroughBit;
        if (decoded is >= Cs2Base and < Cs2Base + CsSize)
        {
            return _console32X.ReadFrameBufferWordForSh2((decoded - Cs2Base) & 0x1FFFF);
        }

        return (ushort)((ReadByte(address) << 8) | ReadByte(address + 1));
    }

    public uint ReadLong(uint address) => ((uint)ReadWord(address) << 16) | ReadWord(address + 2);

    public void WriteByte(uint address, byte value)
    {
        uint decoded = address & ~CacheThroughBit;

        if (decoded < Cs1Base) // CS0
        {
            if (decoded is >= AdapterRegLow and <= AdapterRegHigh)
            {
                _console32X.WriteRegisterByteFromSh2(decoded - AdapterRegLow, value);
            }
            else if (decoded is >= VdpRegLow and <= VdpRegHigh)
            {
                _console32X.WriteVdpControlByteFromSh2(decoded - VdpRegLow, value);
            }
            else if (decoded is >= PaletteLow and <= PaletteHigh)
            {
                _console32X.WritePaletteByteFromSh2(decoded - PaletteLow, value);
            }

            // Boot ROM is read-only from the SH-2's own perspective; anything else in CS0 is ignored.
            return;
        }

        if (decoded is >= Cs2Base and < Cs2Base + CsSize) // CS2: frame buffer (byte writes behave
                                                            // the same in both windows, see WriteFrameBufferByte's own remarks)
        {
            _console32X.WriteFrameBufferByteFromSh2((decoded - Cs2Base) & 0x1FFFF, value);
            return;
        }

        if (decoded is >= Cs3Base and < Cs3Base + CsSize) // CS3: SDRAM
        {
            _console32X.Sdram[(decoded - Cs3Base) & 0x0003_FFFF] = value;
            return;
        }

        // CS1 (cartridge ROM, read-only): ignored.
    }

    public void WriteWord(uint address, ushort value)
    {
        uint decoded = address & ~CacheThroughBit;
        if (decoded is >= Cs2Base and < Cs2Base + CsSize)
        {
            uint local = decoded - Cs2Base;
            bool overwrite = (local & FrameBufferOverwriteBit) != 0;
            _console32X.WriteFrameBufferWordFromSh2(local & 0x1FFFF, overwrite, value);
            return;
        }

        WriteByte(address, (byte)(value >> 8));
        WriteByte(address + 1, (byte)value);
    }

    public void WriteLong(uint address, uint value)
    {
        WriteWord(address, (ushort)(value >> 16));
        WriteWord(address + 2, (ushort)value);
    }
}
