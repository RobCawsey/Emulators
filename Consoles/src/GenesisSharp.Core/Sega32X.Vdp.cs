namespace GenesisSharp.Core;

/// <summary>Phase 3 of the in-progress Sega 32X extension (see ARCHITECTURE.md §4a.1) — the frame
/// buffer, palette, and display-mode register block, plus the compositing rule that overlays the
/// 32X's own output onto the Genesis VDP's, wired into <see cref="Vdp.External32XPixelBlend"/>.
/// Packed Pixel and Direct Color display modes only; Run Length is deferred (degrades to blank
/// rather than rendering garbage — see <see cref="TryGetPixel"/>).
///
/// Ground truth: <c>reference/PicoDrive/picodrive/pico/32x/draw.c</c> and <c>32x.c</c>, cited
/// per-member below. Like the rest of this class, always live but inert for a non-32X ROM:
/// <see cref="TryGetPixel"/> returns false on its very first check whenever the display mode is
/// off (the power-on default), so nothing here is observable to a game that never touches these
/// registers.</summary>
public sealed partial class Sega32X
{
    // Bit positions within VdpRegs[0]/[5] (pico_int.h:603-613).
    private const ushort PriBit = 1 << 7;
    private const ushort MxMask = 0x3;
    private const ushort SftBit = 1 << 0;
    private const ushort VBlkBit = 1 << 15;
    private const ushort HBlkBit = 1 << 14;
    private const ushort PenBit = 1 << 13;
    private const ushort NFenBit = 1 << 1;
    private const ushort FsBit = 1 << 0;

    /// <summary>VdpRegs[0] bit 15 (word-level; the byte a 68000/SH-2 read of offset 0 actually
    /// sees is that word's high byte, so this is bit 7 there). Genuinely active-low, same "n"
    /// prefix convention as <see cref="NCartBit"/>/nRES: confirmed against PicoDrive's own
    /// set/clear condition, <c>if (!Pico.m.pal) vdp_regs[0] |= P32XV_nPAL; else vdp_regs[0] &amp;=
    /// ~P32XV_nPAL;</c> (<c>32x.c:134-137</c>) — the bit is SET for NTSC, CLEARED for PAL, exactly
    /// backwards from what the name reads as at a glance if you don't clock the "n" prefix. Left
    /// clear (the same mistake <see cref="NCartBit"/> made before it was caught) made a real 32X
    /// title's own region/hardware sanity check treat an NTSC system as PAL — confirmed via a
    /// live, verified boot trace, and initially misdiagnosed as a <see
    /// cref="GenesisConsole.VersionRegisterValue"/> polarity bug instead, since both bits feed
    /// the same check and either one being wrong looks identical from that check's own pass/fail
    /// outcome alone. GenesisSharp is NTSC/224-line only (see <see
    /// cref="V28LineTableOffset"/>'s remarks), so this is unconditionally set at reset — there is
    /// no PAL mode for it to ever need clearing.</summary>
    private const ushort NPalBit = 1 << 15;

    /// <summary>Fixed offset between frame-buffer line-table index 0 and the first visible
    /// scanline in 224-line (V28) mode (confirmed at <c>32x.c:258-260</c>; the 240-line/V30 case
    /// there uses 0 instead, but GenesisSharp is NTSC/224-line only, so that branch never
    /// applies).</summary>
    private const int V28LineTableOffset = 8;

    /// <summary>Confirmed directly against source (<c>draw.c:18-19,148</c>: <c>pmd += H32_OFFSET</c>
    /// where <c>pmd</c> is the *Genesis*-side pointer) — for Genesis x-coordinate <c>x</c> in H32
    /// mode, the corresponding 32X frame-buffer pixel is at index <c>x-4</c>.</summary>
    private const int H32PixelOffset = 4;

    /// <summary>The `$A15180-$A1519F` (68k) / `$4100-$411F` (SH-2) register block: offset 0 =
    /// display-mode/PRI/Mx, offset 2 = SFT, offset 4 = fill length, offset 6 = fill start address,
    /// offset 8 = fill data (write triggers the autofill burst), offset 0xA = FBCR
    /// (VBLK/HBLK/PEN/nFEN/FS). Confirmed against <c>Pico32x.vdp_regs[0x10]</c>
    /// (<c>pico_int.h:645</c>).</summary>
    public ushort[] VdpRegs { get; } = new ushort[0x10];

    /// <summary>The 32X's own 256-entry CRAM-equivalent (`$A15200-$A153FF` / `$4200-$43FF`).
    /// Confirmed against <c>Pico32xMem.pal[0x100]</c> (<c>pico_int.h:693</c>): 16-bit entries,
    /// bits 4:0/9:5/14:10 = the three color channels (5:5:5), bit 15 = a per-color priority
    /// marker (not a channel) — <c>convert_pal555</c>, <c>draw.c:44-47</c>.</summary>
    public ushort[] Palette { get; } = new ushort[0x100];

    /// <summary>The two 128KB double-buffered frame-buffer banks (confirmed against
    /// <c>Pico32xMem.dram[2][0x20000/2]</c>, <c>pico_int.h:676</c>). Which bank is read for
    /// rendering vs. written by the CPUs is selected by <see cref="DisplayBankIndex"/>/
    /// <see cref="WriteBankIndex"/>, driven by FBCR's <c>FS</c> bit.</summary>
    public byte[][] FrameBuffer { get; } = { new byte[0x20000], new byte[0x20000] };

    private bool _wasVBlank;
    private bool _hasPendingFrameSelect;
    private bool _pendingFrameSelectValue;

    /// <summary>Drives the fake HBLK/nFEN pulse below — incremented every FBCR read, exactly
    /// matching PicoDrive's own mechanism.</summary>
    private byte _blankFakeCounter;

    /// <summary>The bank rendering reads from — FBCR's <c>FS</c> bit directly selects it
    /// (confirmed: <c>dram[Pico32x.vdp_regs[0x0a/2] &amp; P32XV_FS]</c>, <c>draw.c:145</c>).</summary>
    private int DisplayBankIndex => (VdpRegs[5] & FsBit) != 0 ? 1 : 0;

    /// <summary>The bank both SH-2s' CS2 window and the 68000's `$840000`/`$860000` windows
    /// actually read/write — always the bank <em>not</em> currently selected for display, so
    /// software can draw the next frame without corrupting what's on screen.</summary>
    private int WriteBankIndex => 1 - DisplayBankIndex;

    private void ResetVdp()
    {
        Array.Clear(VdpRegs);
        Array.Clear(Palette);
        Array.Clear(FrameBuffer[0]);
        Array.Clear(FrameBuffer[1]);
        // Power-on default confirmed against PicoPower32x (32x.c:224): VBLK|PEN set. _wasVBlank
        // starts true to match -- otherwise the first real "exit vblank" transition (which
        // happens on scanline 0, right after reset, since active display starts immediately)
        // would never fire, leaving VBLK/PEN incorrectly stuck set through the entire first frame.
        VdpRegs[5] = VBlkBit | PenBit;
        // NPalBit set unconditionally -- see its own remarks: active-low, SET means NTSC, and
        // GenesisSharp has no PAL mode to ever need it cleared.
        VdpRegs[0] = NPalBit;
        _wasVBlank = true;
        _hasPendingFrameSelect = false;
        _blankFakeCounter = 0;
    }

    /// <summary>Called once per scanline by <c>GenesisConsole.RunScanline</c> — drives VBLK/PEN
    /// and applies any deferred <c>FS</c> bank swap on the blank-start edge. Confirmed against
    /// PicoDrive's <c>p32x_start_blank</c>/<c>p32x_end_blank</c> (<c>32x.c:316-346</c>), which are
    /// themselves called from the exact same call sites the Genesis VDP's own vblank status bits
    /// are set/cleared from — VBLK genuinely mirrors real vblank timing, not an independent 32X
    /// clock.</summary>
    public void UpdateBlankingState(bool isVBlank)
    {
        if (isVBlank && !_wasVBlank)
        {
            VdpRegs[5] |= (ushort)(VBlkBit | PenBit);
            if (_hasPendingFrameSelect)
            {
                ApplyFrameSelect(_pendingFrameSelectValue);
                _hasPendingFrameSelect = false;
            }
        }
        else if (!isVBlank && _wasVBlank)
        {
            VdpRegs[5] &= unchecked((ushort)~VBlkBit);
            if ((VdpRegs[0] & MxMask) != 0)
            {
                VdpRegs[5] &= unchecked((ushort)~PenBit);
            }
        }

        _wasVBlank = isVBlank;
    }

    private void ApplyFrameSelect(bool fs)
    {
        VdpRegs[5] = fs ? (ushort)(VdpRegs[5] | FsBit) : (ushort)(VdpRegs[5] & ~FsBit);
    }

    /// <summary>Resolves the 32X layer's contribution to one Genesis pixel, implementing the
    /// compositing rule confirmed against <c>do_line_pp</c>/<c>do_line_dc</c>
    /// (<c>draw.c:62-105</c>) — deliberately *not* "32X always wins": the 32X pixel shows
    /// unconditionally wherever the Genesis plane resolved to its own backdrop color, and
    /// otherwise only when its priority bit (after <c>PRI</c>'s inversion) is set. Wired to
    /// <see cref="Vdp.External32XPixelBlend"/> by <c>GenesisConsole</c>.</summary>
    public bool TryGetPixel(int x, int y, bool isGenesisBackdrop, bool isH32, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        int mode = VdpRegs[0] & MxMask;
        if (mode is 0 or 3) // 0 = off; 3 = Run Length, deferred -- degrade to "layer off" rather than render garbage.
        {
            return false;
        }

        int pixelX = isH32 ? x - H32PixelOffset : x;
        if (pixelX < 0)
        {
            return false;
        }

        byte[] displayBank = FrameBuffer[DisplayBankIndex];
        int lineIndex = y + V28LineTableOffset;
        if ((uint)(lineIndex * 2 + 1) >= (uint)displayBank.Length)
        {
            return false;
        }

        int lineWordOffset = (displayBank[lineIndex * 2] << 8) | displayBank[lineIndex * 2 + 1];
        int lineByteOffset = lineWordOffset * 2;

        ushort raw15;
        bool priorityBit;

        if (mode == 1) // Packed Pixel: 1 byte/pixel, palette index.
        {
            int byteOffset = lineByteOffset + ((VdpRegs[1] & SftBit) != 0 ? 1 : 0) + pixelX;
            if ((uint)byteOffset >= (uint)displayBank.Length)
            {
                return false;
            }

            ushort paletteEntry = Palette[displayBank[byteOffset]];
            priorityBit = (paletteEntry & 0x8000) != 0;
            raw15 = (ushort)(paletteEntry & 0x7FFF);
        }
        else // mode == 2, Direct Color: 1 word/pixel, raw 5:5:5 + priority bit, no SFT/palette involved.
        {
            int byteOffset = lineByteOffset + pixelX * 2;
            if ((uint)(byteOffset + 1) >= (uint)displayBank.Length)
            {
                return false;
            }

            ushort raw = (ushort)((displayBank[byteOffset] << 8) | displayBank[byteOffset + 1]);
            priorityBit = (raw & 0x8000) != 0;
            raw15 = (ushort)(raw & 0x7FFF);
        }

        if ((VdpRegs[0] & PriBit) != 0)
        {
            priorityBit = !priorityBit;
        }

        if (!isGenesisBackdrop && !priorityBit)
        {
            return false; // Genesis pixel wins.
        }

        (r, g, b) = Decode555(raw15);
        return true;
    }

    /// <summary>Converts a 5:5:5 value to RGB24. The source only confirms relative bit
    /// *positions* (low/mid/high 5 bits), never which physical channel each one is — treated here
    /// as R/G/B in that low-to-high order, a labeling choice this core makes, not a hardware fact
    /// PicoDrive states.</summary>
    private static (byte R, byte G, byte B) Decode555(ushort value)
    {
        static byte Scale(int component) => (byte)(component * 255 / 31);
        return (Scale(value & 0x1F), Scale((value >> 5) & 0x1F), Scale((value >> 10) & 0x1F));
    }

    // ---- 68000-side register/palette/frame-buffer access ----

    public byte ReadVdpControlByteFor68k(uint offset) => ReadVdpControlByte(offset);

    public void WriteVdpControlByteFrom68k(uint offset, byte value)
    {
        if ((Regs[0] & FmBit) != 0) // FM=0 -> 68000 owns vdp_regs (confirmed memory.c:1093-1097).
        {
            return;
        }

        WriteVdpControlByte(offset, value);
    }

    public byte ReadPaletteByteFor68k(uint offset) => ReadPaletteByte(offset);

    public void WritePaletteByteFrom68k(uint offset, byte value)
    {
        if ((Regs[0] & FmBit) != 0)
        {
            return;
        }

        WritePaletteByte(offset, value);
    }

    public byte ReadFrameBufferByteFor68k(uint offset) => ReadFrameBufferByte(offset);

    public ushort ReadFrameBufferWordFor68k(uint offset) => ReadFrameBufferWord(offset);

    public void WriteFrameBufferByteFrom68k(uint offset, byte value) => WriteFrameBufferByte(offset, value);

    public void WriteFrameBufferWordFrom68k(uint offset, bool overwrite, ushort value) =>
        WriteFrameBufferWord(offset, overwrite, value);

    // ---- SH-2-side register/palette/frame-buffer access ----

    internal byte ReadVdpControlByteForSh2(uint offset) => ReadVdpControlByte(offset);

    internal void WriteVdpControlByteFromSh2(uint offset, byte value)
    {
        if ((Regs[0] & FmBit) == 0) // FM=1 -> SH-2 owns vdp_regs (the inverse of the 68k gate).
        {
            return;
        }

        WriteVdpControlByte(offset, value);
    }

    internal byte ReadPaletteByteForSh2(uint offset) => ReadPaletteByte(offset);

    internal void WritePaletteByteFromSh2(uint offset, byte value)
    {
        if ((Regs[0] & FmBit) == 0)
        {
            return;
        }

        WritePaletteByte(offset, value);
    }

    internal byte ReadFrameBufferByteForSh2(uint offset) => ReadFrameBufferByte(offset);

    internal ushort ReadFrameBufferWordForSh2(uint offset) => ReadFrameBufferWord(offset);

    internal void WriteFrameBufferByteFromSh2(uint offset, byte value) => WriteFrameBufferByte(offset, value);

    internal void WriteFrameBufferWordFromSh2(uint offset, bool overwrite, ushort value) =>
        WriteFrameBufferWord(offset, overwrite, value);

    // ---- Shared implementations ----

    private byte ReadVdpControlByte(uint offset)
    {
        if (offset > 0x1F)
        {
            return 0xFF;
        }

        if (offset is 0xA or 0xB) // FBCR read -- HBLK/nFEN's fake pulse advances on every read.
        {
            _blankFakeCounter++;
            ApplyFakeBlankBits();
        }

        ushort word = VdpRegs[offset >> 1];
        return (offset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    /// <summary>Confirmed as an intentional, admitted hack, not a shortcut invented here: real
    /// hardware's HBLK/nFEN behavior here is poorly understood even by PicoDrive's own author
    /// ("what's the deal with that?", <c>memory.c:664-668</c>), which fakes both bits from one
    /// free-running counter incremented on every FBCR read. Replicated identically.</summary>
    private void ApplyFakeBlankBits()
    {
        bool hblk = (_blankFakeCounter & 4) != 0;
        bool nfen = (_blankFakeCounter & 7) == 0;
        ushort fbcr = VdpRegs[5];
        fbcr = hblk ? (ushort)(fbcr | HBlkBit) : (ushort)(fbcr & ~HBlkBit);
        fbcr = nfen ? (ushort)(fbcr | NFenBit) : (ushort)(fbcr & ~NFenBit);
        VdpRegs[5] = fbcr;
    }

    private void WriteVdpControlByte(uint offset, byte value)
    {
        if (offset > 0x1F)
        {
            return;
        }

        if (offset == 0) // VdpRegs[0]'s high byte -- NPalBit (bit 15 of the word, bit 7 here) is
                          // its only named bit and is genuinely read-only hardware status, not
                          // software-writable storage. Confirmed against PicoDrive's own
                          // p32x_vdp_write8: there is no case 0x00 handler at all, so a real
                          // 68000/SH-2 write to this byte is simply discarded outright -- and a
                          // *word*-sized write (the common case; a real 32X title's own boot code
                          // clears this whole register with one MOVE.W) falls through to the
                          // case 0x01 (low-byte) handler instead via p32x_vdp_write16's own
                          // `a |= 1` remap, which explicitly re-preserves NPalBit even though it
                          // otherwise looks like a full-word overwrite -- i.e. this bit survives
                          // every software write path real hardware has, not just some of them.
                          // Found the hard way: an earlier attempt to fix a real 32X title's own
                          // hardware-detection deadlock via a plain reset-time default (no write
                          // protection) got silently clobbered back to 0 by this exact register's
                          // own early-boot clear, right before the check that reads it again.
        {
            return;
        }

        if (offset == 0xB) // FBCR's low byte -- only FS is meaningfully writable; VBLK/HBLK/PEN/
                            // nFEN are status bits software never legitimately writes.
        {
            bool requestedFs = (value & FsBit) != 0;
            if (requestedFs != ((VdpRegs[5] & FsBit) != 0))
            {
                bool blanking = (VdpRegs[5] & VBlkBit) != 0 || (VdpRegs[0] & MxMask) == 0;
                if (blanking)
                {
                    ApplyFrameSelect(requestedFs);
                }
                else
                {
                    _hasPendingFrameSelect = true;
                    _pendingFrameSelectValue = requestedFs;
                }
            }

            return;
        }

        int index = (int)(offset >> 1);
        ushort word = VdpRegs[index];
        VdpRegs[index] = (offset & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);

        if (offset == 9) // Low byte of the fill-data register -- writing it triggers the burst,
                          // matching real software's usual word-sized write to this register.
        {
            PerformAutofill();
        }
    }

    /// <summary>Fills the write-target bank with the fill-data register's value, starting at the
    /// fill-start-address register, for (fill-length + 1) words — confirmed against PicoDrive
    /// only at the level of "writing the fill-data register performs an autofill burst into
    /// dram[FS^1]" (<c>memory.c:721-737</c>); the exact inclusive/exclusive length convention
    /// wasn't directly quoted in this project's own research pass, so the "+1" here is a
    /// reasonable but not byte-exact-confirmed interpretation. No multi-cycle timing is modeled —
    /// the burst completes instantly on write, matching this area's general "fake it" treatment
    /// (real hardware timing here is likewise not well understood — see the HBLK remarks above).</summary>
    private void PerformAutofill()
    {
        byte[] bank = FrameBuffer[WriteBankIndex];
        int length = VdpRegs[2] & 0xFF;
        ushort fillValue = VdpRegs[4];
        ushort wordOffset = VdpRegs[3];

        for (int i = 0; i <= length; i++)
        {
            int byteOffset = (ushort)(wordOffset + i) * 2;
            if (byteOffset + 1 < bank.Length)
            {
                bank[byteOffset] = (byte)(fillValue >> 8);
                bank[byteOffset + 1] = (byte)fillValue;
            }
        }
    }

    private byte ReadPaletteByte(uint offset)
    {
        if (offset > 0x1FF)
        {
            return 0;
        }

        ushort word = Palette[offset >> 1];
        return (offset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    private void WritePaletteByte(uint offset, byte value)
    {
        if (offset > 0x1FF)
        {
            return;
        }

        int index = (int)(offset >> 1);
        ushort word = Palette[index];
        Palette[index] = (offset & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);
    }

    private byte ReadFrameBufferByte(uint offset)
    {
        byte[] bank = FrameBuffer[WriteBankIndex];
        uint local = offset & 0x1FFFF;
        return local < (uint)bank.Length ? bank[local] : (byte)0;
    }

    private ushort ReadFrameBufferWord(uint offset) =>
        (ushort)((ReadFrameBufferByte(offset) << 8) | ReadFrameBufferByte(offset + 1));

    /// <summary>Confirmed in Phase 2's own research pass (<c>memory.c</c>'s <c>sh2_write8_dram*</c>
    /// macros): a byte write of value zero is silently dropped in *both* the normal and
    /// "overwrite" frame-buffer windows, not just the overwrite one — this single rule is what
    /// gives both windows their documented behavior for byte-sized accesses, so no separate
    /// overwrite-vs-normal branch is needed here the way <see cref="WriteFrameBufferWord"/> needs
    /// one.</summary>
    private void WriteFrameBufferByte(uint offset, byte value)
    {
        if (value == 0)
        {
            return;
        }

        byte[] bank = FrameBuffer[WriteBankIndex];
        uint local = offset & 0x1FFFF;
        if (local < (uint)bank.Length)
        {
            bank[local] = value;
        }
    }

    private void WriteFrameBufferWord(uint offset, bool overwrite, ushort value)
    {
        byte[] bank = FrameBuffer[WriteBankIndex];
        uint local = offset & 0x1FFFF;
        if (local + 1 >= (uint)bank.Length)
        {
            return;
        }

        byte hi = (byte)(value >> 8);
        byte lo = (byte)value;
        if (overwrite)
        {
            if (hi != 0) bank[local] = hi;
            if (lo != 0) bank[local + 1] = lo;
        }
        else
        {
            bank[local] = hi;
            bank[local + 1] = lo;
        }
    }
}
