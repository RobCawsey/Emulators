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

    /// <summary>The SH-2's own on-chip peripheral block — real, fixed silicon addresses
    /// ($FFFFFE00-$FFFFFFFF, confirmed against PicoDrive's own <c>sh2soc.c:9-24</c> map comment)
    /// that exist regardless of the CS0-CS3 *external* bus decode above; unlike everything else
    /// in this class, these aren't part of the cartridge/32X-adapter address space at all — they're
    /// inside the SH-2 chip itself. Previously entirely unmapped here (fell through every CS0-CS3
    /// check and landed on the same "open bus reads 0, writes dropped" catch-all CS1 ROM gets) —
    /// a real, silent gap: <c>ARCHITECTURE.md</c>'s "no on-chip cache, DMAC, timers, or serial
    /// are modeled" note frames this as a CPU-core-level simplification, but nothing at the bus
    /// level it's actually deferred to ever said so either. Only the DMAC sub-block is
    /// implemented now (see <see cref="TryTriggerDma"/>) — SCI/WDT/DIVU/the free-running timer
    /// remain genuinely unmapped, a real, still-open gap, not silently pretended-correct; plain
    /// byte storage here lets software that merely pokes at other peripheral registers (without
    /// depending on their live behavior) proceed instead of silently losing every write.</summary>
    private readonly byte[] _peripheralRegs = new byte[0x200];

    /// <summary>Save-state-only access to <see cref="_peripheralRegs"/> -- internal, not public,
    /// matching how the rest of this class's state is only reachable through <see
    /// cref="Sega32X"/>'s own internal fields (<see cref="_masterSh2Bus"/>/<see
    /// cref="_slaveSh2Bus"/>), never the public <see cref="CpuSh2.IBus"/> surface.</summary>
    internal byte[] PeripheralRegs => _peripheralRegs;

    private const uint PeripheralBase = 0xFFFF_FE00;

    // DMAC channel register byte offsets within _peripheralRegs, confirmed against PicoDrive's
    // own struct dmac layout (sh2soc.c:33-62): base offset 0x180 within the peripheral block is
    // real address $FFFFFF80, matching this file's own "f80-fb3 DMAC" map comment.
    private const int Dmac0Sar = 0x180;
    private const int Dmac0Dar = 0x184;
    private const int Dmac0Tcr = 0x188;
    private const int Dmac0Chcr = 0x18C;
    private const int Dmac1Sar = 0x190;
    private const int Dmac1Dar = 0x194;
    private const int Dmac1Tcr = 0x198;
    private const int Dmac1Chcr = 0x19C;
    private const int DmaorOffset = 0x1B0;

    private const uint ChcrDe = 1 << 0; // DMA enable
    private const uint ChcrTe = 1 << 1; // transfer end
    private const uint ChcrAr = 1 << 9; // auto-request (vs. external DREQ-driven)
    private const uint DmaorDme = 1 << 0; // DMA master enable

    /// <summary>A defensive cap on one DMA channel's transfer count, not a real hardware limit —
    /// TCR is a real 24-bit field (up to ~16.7M transfer units) but this project's own SDRAM is
    /// only 256KB total, so any single 32X-relevant transfer is bounded well under this; guards
    /// against a garbage/uninitialized TCR value turning into a multi-second freeze mid-<see
    /// cref="Sh2.Step"/> rather than a real transfer.</summary>
    private const uint MaxDmaTransferUnits = 0x0004_0000;

    // SCI (serial communication interface) register byte offsets within _peripheralRegs,
    // confirmed against PicoDrive's own sci_trigger/sh2_peripheral_write8 (sh2soc.c:315-392):
    // SMR=0x00 (unused by the trigger logic, storage only), BRR=0x01, SCR=0x02, TDR=0x03,
    // SSR=0x04, RDR=0x05. The IRQ vector-control block used by the trigger below lives much
    // further out (real address $FFFFFE60+, matching this file's own "e60-e68 VCRx" map
    // comment) -- SCI's own level and TX/RX vector registers specifically.
    private const int SciScr = 0x02;
    private const int SciTdr = 0x03;
    private const int SciSsr = 0x04;
    private const int SciRdr = 0x05;
    private const uint ScrRe = 1 << 4; // receive enable
    private const uint ScrTe = 1 << 5; // transmit enable
    private const uint ScrRie = 1 << 6; // receive-interrupt enable
    private const uint ScrTie = 1 << 7; // transmit-interrupt enable
    private const uint SsrRdrf = 1 << 6; // receive data register full
    private const uint SsrTdre = 1 << 7; // transmit data register empty
    private const int VcrLevel = 0x60;
    private const int VcrRxVector = 0x63;
    private const int VcrTxVector = 0x64;

    private readonly Sega32X _console32X;
    private readonly bool _isSlave;

    /// <summary>This bus's own SH-2 core and the *other* core's bus/SH-2 -- needed only for
    /// <see cref="TrySciTrigger"/>'s cross-core handshake (reading the other core's own SCI
    /// registers, writing into its RDR, and raising an interrupt directly on either core).
    /// Necessarily set after construction (<see cref="Sega32X"/>'s constructor links both
    /// buses together once all four objects -- both buses, both <see cref="Sh2"/> cores --
    /// exist), not passed to this class's own constructor, since each <see cref="Sh2"/>
    /// instance itself needs its bus to already exist first.</summary>
    private Sh2? _ownSh2;
    private Sega32XSh2Bus? _otherBus;
    private Sh2? _otherSh2;

    public Sega32XSh2Bus(Sega32X console32X, bool isSlave)
    {
        _console32X = console32X;
        _isSlave = isSlave;
    }

    internal void LinkCores(Sh2 ownSh2, Sega32XSh2Bus otherBus, Sh2 otherSh2)
    {
        _ownSh2 = ownSh2;
        _otherBus = otherBus;
        _otherSh2 = otherSh2;
    }

    /// <summary>Confirmed against PicoDrive's own <c>sh2_peripheral_reset</c> (<c>sh2soc.c:
    /// 242-250</c>): the whole block zeroed, then a handful of SCI/timer registers given their
    /// real power-on non-zero defaults. Only the *defaults* are ported here — the SCI/timer
    /// *behavior* those registers would otherwise drive isn't implemented (see this class's own
    /// remarks on <see cref="_peripheralRegs"/>), but a real ROM that merely reads these specific
    /// bytes back before deciding whether to touch that hardware at all should see the same
    /// values real hardware boots with.</summary>
    public void ResetPeripherals()
    {
        Array.Clear(_peripheralRegs);
        _peripheralRegs[0x001] = 0xFF; // SCI BRR
        _peripheralRegs[0x003] = 0xFF; // SCI TDR
        _peripheralRegs[0x004] = 0x84; // SCI SSR
        _peripheralRegs[0x011] = 0x01; // TIER
        _peripheralRegs[0x017] = 0xE0; // TOCR
    }

    public byte ReadByte(uint address)
    {
        if (address >= PeripheralBase) // fixed silicon address, not part of the CS0-CS3 space at all
        {
            uint offset = address - PeripheralBase;
            return offset < (uint)_peripheralRegs.Length ? _peripheralRegs[offset] : (byte)0;
        }

        uint decoded = address & ~CacheThroughBit;

        if (decoded < Cs1Base) // CS0: boot ROM + adapter sysregs + vdp_regs + palette
        {
            if (decoded is >= AdapterRegLow and <= AdapterRegHigh)
            {
                return _console32X.ReadControlByteForSh2(decoded - AdapterRegLow, _isSlave);
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
        if (address >= PeripheralBase) // fixed silicon address, not part of the CS0-CS3 space at all
        {
            uint offset = address - PeripheralBase;
            if (offset >= (uint)_peripheralRegs.Length)
            {
                return;
            }

            byte oldValue = _peripheralRegs[offset];

            // SSR (serial status register) has real clear-on-write-0 status-bit semantics, not
            // plain storage -- a write can never *set* TDRE/RDRF/etc, only clear a bit that was
            // already 1 by writing 0 to it (bit 0 is the sole exception, a directly-writable
            // control bit, not a status flag). Ported exactly from PicoDrive's own masking rule
            // (sh2soc.c:370-374: `d = (old & (d | 0x06)) | (d & 1);`) rather than reformulated,
            // matching this project's general "port handshake-critical logic literally" judgment.
            if (offset == SciSsr)
            {
                value = (byte)((oldValue & (value | 0x06)) | (value & 1));
            }

            _peripheralRegs[offset] = value;

            if (offset == SciScr && (oldValue & ScrTe) == 0 && (value & ScrTe) != 0)
            {
                // TE (transmit enable) just got set -- confirmed trigger point against PicoDrive's
                // own sh2_peripheral_write8 (sh2soc.c:363-367, "case 0x002... if TE being set").
                TrySciTrigger();
            }
            else if (offset == SciSsr)
            {
                // Any SSR write re-checks the handshake too (sh2soc.c:370-373) -- covers software
                // clearing TDRE by hand to kick off the next byte without re-toggling TE.
                TrySciTrigger();
            }
            else if (offset == Dmac0Chcr + 3 || offset == Dmac1Chcr + 3 || offset == DmaorOffset + 3)
            {
                // DMA triggers on a full 32-bit write completing CHCR0/CHCR1/DMAOR, confirmed
                // against PicoDrive's own sh2_peripheral_write32 (sh2soc.c:469-482, only the
                // 32-bit write path switches on these three addresses at all). This core has no
                // bespoke WriteLong/WriteWord path into the peripheral block (unlike CS2's frame
                // buffer) -- Sh2.WriteLong decomposes into WriteWord-then-WriteWord, which
                // decomposes into WriteByte four times in big-endian byte order, so the *last*
                // byte of any 4-byte-aligned register write lands here last, regardless of
                // whether the caller's own access was a single 32-bit store or four separate byte
                // stores -- checking "did the low byte of one of these three registers just get
                // written" is a simpler, byte-write-order-based stand-in for "did the whole
                // 32-bit register just get written" that doesn't need this class to track access
                // width itself. Slightly more permissive than PicoDrive (a lone 16-bit or 8-bit
                // write to just the low half would also fire this), accepted since real SH-2 code
                // overwhelmingly writes control registers as full longwords.
                TryTriggerDma();
            }

            return;
        }

        uint decoded = address & ~CacheThroughBit;

        if (decoded < Cs1Base) // CS0
        {
            if (decoded is >= AdapterRegLow and <= AdapterRegHigh)
            {
                _console32X.WriteRegisterByteFromSh2(decoded - AdapterRegLow, value, _isSlave);
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

    private uint ReadPeripheralLong(int offset) =>
        ((uint)_peripheralRegs[offset] << 24) | ((uint)_peripheralRegs[offset + 1] << 16) |
        ((uint)_peripheralRegs[offset + 2] << 8) | _peripheralRegs[offset + 3];

    private void WritePeripheralLong(int offset, uint value)
    {
        _peripheralRegs[offset] = (byte)(value >> 24);
        _peripheralRegs[offset + 1] = (byte)(value >> 16);
        _peripheralRegs[offset + 2] = (byte)(value >> 8);
        _peripheralRegs[offset + 3] = (byte)value;
    }

    /// <summary>The on-chip SCI's own genuine SH-2-to-SH-2 hardware handshake: one core's TDR
    /// (transmit data register) copies directly into the *other* core's RDR (receive data
    /// register), no 32X-adapter register or COMM byte involved at all -- a real, separate cross-
    /// core signaling path this project had never modeled at all before now (see <see
    /// cref="_peripheralRegs"/>'s own remarks). Ported line-for-line against PicoDrive's own
    /// <c>sci_trigger</c> (<c>sh2soc.c:315-348</c>), including its exact gating order (transmit
    /// enabled -&gt; TDR actually has unsent data -&gt; other core's receiver enabled) and its
    /// somewhat surprising choice of *which* core's VCR (vector-control) registers supply the
    /// interrupt level/vector for *both* the TX-complete and RX-complete interrupts (the
    /// *receiving* core's own <c>VcrLevel</c>/<c>VcrTxVector</c> for the TX side too, not the
    /// sending core's) -- kept exactly as PicoDrive has it rather than "corrected", per this
    /// project's established "port handshake-critical logic literally, don't get clever"
    /// judgment (see <c>Sega32X.BootStub.cs</c>'s own remarks on the same principle) — a subtly
    /// wrong transcription here would produce something that *looks* plausible but silently
    /// deadlocks or races, exactly the failure mode already investigated at length for this
    /// specific ROM. <see cref="Sh2.RaiseInterrupt"/> (already used for the 32X-adapter's own
    /// five CMD/VINT/HINT/PWM/VRES sources) is reused directly here rather than building a
    /// second, parallel "internal interrupt" delivery path -- it's already a generic
    /// priority-gated raise with no dependency on <see cref="Sega32X.Sh2IrqMask"/> or any other
    /// 32X-adapter-specific state, so SCI's own TIE/RIE enable bits are a sufficient, correct
    /// gate on their own.</summary>
    private void TrySciTrigger()
    {
        if (_ownSh2 is null || _otherBus is null || _otherSh2 is null)
        {
            return; // not yet linked -- see LinkCores' own remarks; shouldn't happen post-construction
        }

        if ((_peripheralRegs[SciScr] & ScrTe) == 0)
        {
            return; // transmitter not enabled
        }

        if ((_peripheralRegs[SciSsr] & SsrTdre) != 0)
        {
            return; // TDR already empty -- nothing queued to send
        }

        byte[] otherRegs = _otherBus._peripheralRegs;
        if ((otherRegs[SciScr] & ScrRe) == 0)
        {
            return; // other core's receiver not enabled
        }

        otherRegs[SciRdr] = _peripheralRegs[SciTdr];
        _peripheralRegs[SciSsr] |= (byte)SsrTdre;
        otherRegs[SciSsr] |= (byte)SsrRdrf;

        if ((_peripheralRegs[SciScr] & ScrTie) != 0)
        {
            int level = otherRegs[VcrLevel] >> 4;
            int vector = otherRegs[VcrTxVector] & 0x7F;
            _ownSh2.RaiseInterrupt(level, vector);
        }

        if ((otherRegs[SciScr] & ScrRie) != 0)
        {
            int level = otherRegs[VcrLevel] >> 4;
            int vector = otherRegs[VcrRxVector] & 0x7F;
            _otherSh2.RaiseInterrupt(level, vector);
        }
    }

    /// <summary>Checks DMAOR's master-enable bit, then each of the two DMAC channels' own
    /// enable/complete state, exactly matching PicoDrive's own trigger site (<c>sh2soc.c:
    /// 469-482</c>): a write to CHCR0/CHCR1/DMAOR re-checks *both* channels every time (not just
    /// the one whose register was just written), since a DMAOR write alone can arm an
    /// already-configured channel.</summary>
    private void TryTriggerDma()
    {
        uint dmaor = ReadPeripheralLong(DmaorOffset);
        if ((dmaor & DmaorDme) == 0)
        {
            return;
        }

        TryTriggerDmaChannel(Dmac0Sar, Dmac0Dar, Dmac0Tcr, Dmac0Chcr);
        TryTriggerDmaChannel(Dmac1Sar, Dmac1Dar, Dmac1Tcr, Dmac1Chcr);
    }

    /// <summary>Performs one DMAC channel's transfer synchronously and instantly on the
    /// triggering write, matching this project's own established "fake it" treatment for
    /// hardware whose real multi-cycle timing isn't modeled (autofill, PWM's FIFO-and-forget
    /// shape) rather than PicoDrive's own sleep-the-SH-2-until-done state machine, which this
    /// core's <see cref="Sh2.Step"/> has no equivalent hook for. Only auto-request transfers
    /// (<see cref="ChcrAr"/> set) are performed — external-request (DREQ-driven) transfers are a
    /// real, separate, still-unimplemented mechanism (this project's own DREQ adapter-register
    /// block, <c>Sega32X.cs</c> offsets 8-0x13, is storage-only; see the original 32X plan's own
    /// "what's deliberately deferred" note) and are left armed rather than silently completed.
    /// Confirmed against PicoDrive's own <c>dmac_trigger</c> (<c>sh2soc.c:153-190</c>) for the
    /// gating condition (<c>(chcr &amp; (TE|DE)) == DE</c>: only re-fires a channel that's enabled
    /// and hasn't already finished) and <c>dmac_transfer_one</c> (<c>sh2soc.c:85-131</c>) for the
    /// per-unit address stepping (SM/DM 2-bit fields: 00 fixed, 01 increment, 10 decrement, 11
    /// reserved/treated as fixed here). The 16-byte "4-word block" transfer mode (<c>size==3</c>)
    /// is not implemented — falls back to one 32-bit unit at a time, which under-transfers per
    /// step rather than corrupting anything already-decoded correctly; no PicoDrive source access
    /// used it during this project's own research pass.</summary>
    private void TryTriggerDmaChannel(int sarOffset, int darOffset, int tcrOffset, int chcrOffset)
    {
        uint chcr = ReadPeripheralLong(chcrOffset);
        if ((chcr & (ChcrTe | ChcrDe)) != ChcrDe)
        {
            return;
        }

        if ((chcr & ChcrAr) == 0)
        {
            return; // external-request transfer -- not implemented, see this method's own remarks
        }

        uint sar = ReadPeripheralLong(sarOffset);
        uint dar = ReadPeripheralLong(darOffset);
        uint tcr = ReadPeripheralLong(tcrOffset) & 0x00FF_FFFF; // 24-bit count, matches PicoDrive's `tcr &= 0xffffff`
        if (tcr > MaxDmaTransferUnits)
        {
            tcr = MaxDmaTransferUnits; // defensive cap -- see MaxDmaTransferUnits' own remarks
        }

        int transferSize = (int)((chcr >> 10) & 3); // 0=byte, 1=word, 2=long, 3=16-byte block (unimplemented, falls to long)
        int stepSize = transferSize switch { 0 => 1, 1 => 2, _ => 4 };
        uint destinationMode = (chcr >> 14) & 3;
        uint sourceMode = (chcr >> 12) & 3;

        while (tcr > 0)
        {
            switch (transferSize)
            {
                case 0: WriteByte(dar, ReadByte(sar)); break;
                case 1: WriteWord(dar, ReadWord(sar)); break;
                default: WriteLong(dar, ReadLong(sar)); break;
            }

            if (destinationMode == 1) dar += (uint)stepSize;
            else if (destinationMode == 2) dar -= (uint)stepSize;

            if (sourceMode == 1) sar += (uint)stepSize;
            else if (sourceMode == 2) sar -= (uint)stepSize;

            tcr--;
        }

        WritePeripheralLong(sarOffset, sar);
        WritePeripheralLong(darOffset, dar);
        WritePeripheralLong(tcrOffset, 0);
        WritePeripheralLong(chcrOffset, chcr | ChcrTe);

        // IE (bit 2) would raise an SH-2-internal DMA-complete interrupt on real hardware
        // (PicoDrive's dmac_transfer_complete -> dmac_te_irq, sh2soc.c:64-83) -- this core has no
        // equivalent of that second, SH-2-peripheral-driven interrupt path (distinct from the
        // 32X's own five adapter-level sources in Sega32X.Interrupts.cs). Named gap, not silently
        // dropped: TE is still set correctly above, so software that polls CHCR's own TE bit
        // (rather than waiting on the IRQ specifically) will still see the transfer complete.
    }
}
