using GenesisSharp.CpuSh2;

namespace GenesisSharp.Core;

/// <summary>Phases 1-4 of an in-progress Sega 32X extension (see ARCHITECTURE.md §4a and the
/// project's own 32X phased plan): both SH-2s can be released from reset and execute real code
/// against their own memory, exchange data with the 68000 through the shared communication
/// registers, draw into their own frame buffer (<c>Sega32X.Vdp.cs</c>), and drive PWM audio
/// (<c>Sega32X.Pwm.cs</c>). No DREQ/DMAC or 32X-side interrupt routing yet — see the per-method
/// remarks below and this class's own known-gaps notes for exactly what's deferred.
///
/// Ground truth: <c>reference/PicoDrive/picodrive</c>'s <c>pico/32x/32x.c</c> and
/// <c>pico/32x/memory.c</c> (no 32X source exists in genesis-plus-gx). Cited per-member below.
/// Always constructed by <see cref="GenesisConsole"/>, exactly like <see cref="Vdp"/>/
/// <see cref="Ym2612"/>/<see cref="Psg"/> — never null/opt-in. A non-32X ROM simply never writes
/// the ADEN bit, so the SH-2s are never stepped and nothing here is observable to it.</summary>
public sealed partial class Sega32X
{
    // Bit positions within Regs[0] (the adapter/control word), confirmed against PicoDrive's
    // P32XS_*/P32XS2_* #defines (pico/pico_int.h:589-596).
    private const ushort FmBit = 1 << 15;
    private const ushort RenBit = 1 << 7;
    private const ushort NResBit = 1 << 1;
    private const ushort AdenBit = 1 << 0;

    /// <summary>SH-2-visible-only "no cartridge" bit (bit position <c>P32XS2_nCART = 1&lt;&lt;8</c>,
    /// <c>pico_int.h:595</c>) — the "n" prefix is genuinely active-low here, confirmed by
    /// PicoDrive's own set condition: <c>if (Pico.m.ncart_in) sh2_regs[0] |= P32XS2_nCART</c>
    /// (<c>32x.c:131-132</c>), where <c>ncart_in</c> is itself documented as <c>"!cart_in"</c>
    /// (<c>pico_int.h:339</c>) — i.e. the bit is set precisely when NO cartridge is present, not
    /// when one is. Never set in GenesisSharp, since <see cref="GenesisConsole"/> always requires a
    /// <see cref="Cartridge"/> — the opposite of a since-corrected Phase 2 bug that OR'd this bit
    /// in unconditionally (backwards: it made every SH-2-side read claim no cartridge was present,
    /// which is exactly what sends the synthesized boot stub's own cartridge-vs-CD branch down the
    /// wrong path — see <see cref="Sega32X.BootStub.cs"/>).</summary>
    private const ushort NCartBit = 1 << 8;

    private readonly Cartridge _cartridge;

    /// <summary>The whole $A15100-$A1513F 64-byte adapter/control register block as one flat
    /// array (confirmed against PicoDrive's own <c>Pico32x.regs[0x20]</c>, <c>pico_int.h:644</c>):
    /// byte 0 = FM/REN, byte 1 = nRES/ADEN, bytes 0x20-0x2f = COMM0-7, bytes 0x30-0x3f = PWM
    /// (real behavior — see <c>Sega32X.Pwm.cs</c>). Everything else (IRQ control, ROM bank, DREQ,
    /// IRQ-pending-clear) is plain storage for now — see this class's known-gaps remarks.</summary>
    public ushort[] Regs { get; } = new ushort[0x20];

    /// <summary>256KB of SH-2-side work RAM, mapped at CS3 (confirmed against PicoDrive's
    /// <c>Pico32xMem.sdram</c>, <c>pico_int.h:671</c>, and its CS3 mask <c>0x03ffff</c>,
    /// <c>memory.c:2446-2456</c>).</summary>
    public byte[] Sdram { get; } = new byte[0x40000];

    /// <summary>The master SH-2's boot ROM (CS0, below the adapter-register range). Real hardware
    /// ships a real BIOS here; this project doesn't (see this class's known-gaps remarks) — it
    /// starts blank and is <c>public</c> specifically so tests (and, eventually, a real boot-stub
    /// synthesis step) can inject code directly, matching how real boot ROM content is what a real
    /// SH-2 actually executes first out of reset. Sized to match PicoDrive's <c>sh2_rom_m</c>
    /// (<c>pico_int.h:687-688</c>).</summary>
    public byte[] BootRomMaster { get; } = new byte[0x800];

    /// <summary>The slave SH-2's boot ROM — same idea as <see cref="BootRomMaster"/>, sized to
    /// match PicoDrive's <c>sh2_rom_s</c> (<c>pico_int.h:689-692</c>).</summary>
    public byte[] BootRomSlave { get; } = new byte[0x400];

    public Sh2 MasterSh2 { get; }
    public Sh2 SlaveSh2 { get; }

    /// <summary>The same bus each SH-2 above was constructed with, exposed for debug-only peek
    /// reads (the frontend's disassembler needs to read memory around <c>PC</c> without going
    /// through the CPU core itself — see <c>Sh2Disassembler</c>). Typed as the public
    /// <see cref="CpuSh2.IBus"/> interface rather than the concrete (internal)
    /// <see cref="Sega32XSh2Bus"/>, the same way <c>GenesisConsole</c> itself is only ever handed
    /// out as <c>Cpu68000.IBus</c>/<c>CpuZ80.IBus</c> to the equivalent 68000/Z80 disassemblers.</summary>
    public CpuSh2.IBus MasterSh2Bus { get; }
    public CpuSh2.IBus SlaveSh2Bus { get; }

    /// <summary>Same two bus instances as <see cref="MasterSh2Bus"/>/<see cref="SlaveSh2Bus"/>,
    /// kept as the concrete type internally so <see cref="Reset"/> and save-state code can reach
    /// <see cref="Sega32XSh2Bus.ResetPeripherals"/> and the on-chip peripheral register block --
    /// members the public <see cref="CpuSh2.IBus"/> surface deliberately doesn't expose.</summary>
    private readonly Sega32XSh2Bus _masterSh2Bus;
    private readonly Sega32XSh2Bus _slaveSh2Bus;

    /// <summary>True once the 68000 has released the shared reset line (Regs[0] bit1). Both SH-2s
    /// share a single reset line — there is no independent per-core reset bit anywhere in the
    /// register model (confirmed against PicoDrive's <c>p32x_reset_sh2s</c>, which always resets
    /// both cores together, <c>32x.c:161-168</c>).</summary>
    public bool NRes => (Regs[0] & NResBit) != 0;

    /// <summary>True once the 68000 has enabled the 32X subsystem (Regs[0] bit0). Confirmed
    /// against PicoDrive's <c>P32XS_ADEN</c> handling (<c>32x.c:104-159</c>).</summary>
    public bool Aden => (Regs[0] & AdenBit) != 0;

    /// <summary>The exact gating condition PicoDrive checks before running or interrupting either
    /// SH-2 (<c>(regs[0] &amp; (nRES|ADEN)) == (nRES|ADEN)</c>, <c>32x.c:39-40</c>, <c>528-531</c>)
    /// — both bits, not just one.</summary>
    public bool Sh2sReleased => NRes && Aden;

    public Sega32X(Cartridge cartridge)
    {
        _cartridge = cartridge;
        _masterSh2Bus = new Sega32XSh2Bus(this, isSlave: false);
        _slaveSh2Bus = new Sega32XSh2Bus(this, isSlave: true);
        MasterSh2Bus = _masterSh2Bus;
        SlaveSh2Bus = _slaveSh2Bus;
        MasterSh2 = new Sh2(MasterSh2Bus);
        SlaveSh2 = new Sh2(SlaveSh2Bus);
        _masterSh2Bus.LinkCores(MasterSh2, _slaveSh2Bus, SlaveSh2);
        _slaveSh2Bus.LinkCores(SlaveSh2, _masterSh2Bus, MasterSh2);
        PopulateSynthesizedBootRoms();
    }

    /// <summary>Power-on default confirmed against PicoDrive's <c>PicoPower32x</c>
    /// (<c>32x.c:218-225</c>): REN and nRES set, ADEN clear — the SH-2s exist but are held and the
    /// subsystem is disabled until the 68000 explicitly enables it. Boot ROM content is
    /// deliberately *not* cleared here — see <see cref="BootRomMaster"/>'s remarks; it's fixed
    /// hardware content on a real 32X, not something a console reset would erase.</summary>
    public void Reset()
    {
        Array.Clear(Regs);
        Regs[0] = RenBit | NResBit;
        Array.Clear(Sdram);
        // Both SH-2s' interrupt-enable registers are part of what a power-on clears, matching
        // PicoPower32x's own whole-struct `memset(&Pico32x, 0, sizeof(Pico32x))` (32x.c:220) --
        // sh2irq_mask lives inside that struct. Left stale, a reset would carry the previous run's
        // per-core VINT/HINT/CMD/PWM enables straight into the next one.
        Array.Clear(Sh2IrqMask);
        Array.Clear(Sh2IrqPending);
        ResetVdp();
        ResetPwm();
        ResetHInt();
        _masterSh2Bus.ResetPeripherals();
        _slaveSh2Bus.ResetPeripherals();
        MasterSh2.Reset();
        SlaveSh2.Reset();
        SynthesizeSh2BootStateFromCartridge();

        // Last, and deliberately so: Sh2.Reset clears PendingInterruptLevel, so this would be
        // erased if it ran any earlier. See RaiseVResInterrupt's own remarks for why the whole-
        // system reset path raises VRES while the nRES 0->1 edge (which resets both cores a few
        // lines away, in WriteControlByteFrom68k) correctly does not.
        RaiseVResInterrupt();
    }

    /// <summary>68000-side byte read of the adapter/control block ($A15100-$A1513F, offset
    /// 0-0x3F). Plain storage read — see <see cref="Regs"/>'s remarks for which offsets currently
    /// have real behavior beyond storage.</summary>
    public byte ReadControlByteFor68k(uint offset)
    {
        if (offset > 0x3F)
        {
            return 0xFF;
        }

        if (offset >= 0x30)
        {
            return ReadPwmByte(offset);
        }

        return ReadRegByte(offset);
    }

    /// <summary>68000-side byte write of the adapter/control block. Offsets 0 and 1 are masked
    /// writes, not plain storage — confirmed against PicoDrive's own <c>p32x_reg_write8</c>
    /// (<c>memory.c:406-426</c>, "writable bits tested" per its own comment):
    /// <list type="bullet">
    /// <item>Offset 0: only <c>FM</c> (bit 7) is 68000-writable; every other bit in that byte is
    /// discarded on write.</item>
    /// <item>Offset 1: only <c>nRES</c>/<c>ADEN</c> (bits 1/0) are ever stored from the written
    /// value — <c>REN</c> (bit 7) and everything else in that byte survive a write completely
    /// untouched. A real, previously-shipped bug here (a naive full-byte overwrite) is exactly
    /// what a real 32X title's own boot code exposed: it polls <c>REN</c>, and a write here that
    /// clobbers it back to 0 means that poll never sees it set again, hanging forever. Disabling
    /// <c>ADEN</c> (a 1→0 transition) additionally forces <c>nRES</c> back to released in the
    /// stored value, confirmed against PicoDrive's own <c>d |= P32XS_nRES</c> there — a real
    /// subsystem shutdown leaves the SH-2s not-held but the whole subsystem inert, the same
    /// shape as the power-on default. A 0→1 transition of <c>nRES</c> (when <c>ADEN</c> isn't
    /// also being cleared in the same write) resets both SH-2s together — this is 68000-exclusive;
    /// nothing on the SH-2 side can assert or release its own reset.</item>
    /// </list></summary>
    public void WriteControlByteFrom68k(uint offset, byte value)
    {
        if (offset > 0x3F)
        {
            return;
        }

        if (offset >= 0x30)
        {
            WritePwmByte(offset, value, fromSh2: false);
            return;
        }

        if (offset == 0)
        {
            // FmBit (0x8000) is word-relative; offset 0 is that word's high byte, so FM's
            // position within this byte is bit 7 (0x80), not the raw word-level mask.
            WriteRegByte(0, (byte)(value & (FmBit >> 8)));
            return;
        }

        if (offset == 1)
        {
            byte oldByte1 = ReadRegByte(1);
            bool adenWasSet = (oldByte1 & AdenBit) != 0;
            bool adenNowSet = (value & AdenBit) != 0;

            if (adenWasSet && !adenNowSet)
            {
                value |= (byte)NResBit;
            }
            else if ((oldByte1 & NResBit) == 0 && (value & NResBit) != 0)
            {
                MasterSh2.Reset();
                SlaveSh2.Reset();
                SynthesizeSh2BootStateFromCartridge();

                // Sh2.Reset() deasserts each core's interrupt-request pins, but resetting a CPU
                // does not make an external device stop driving them -- whatever is in
                // Sh2IrqPending is still asserting. Re-drive from that (the source of truth) so the
                // pins reflect reality rather than a stale zero. Deliberately not a clear: nRES
                // resets the CPUs, not the 32X's interrupt controller.
                UpdateInterruptRequestLevels();
            }

            byte preserved = (byte)(oldByte1 & ~(NResBit | AdenBit));
            byte updated = (byte)(preserved | (value & (NResBit | AdenBit)));
            WriteRegByte(1, updated);
            return;
        }

        if (offset == 2) // high byte of the "irq ctl" word -- ignored, always 0 (memory.c:427-428)
        {
            return;
        }

        if (offset == 3) // irq ctl (low byte): CMD IRQ request bits, bit 0 = request-to-master,
                          // bit 1 = request-to-slave (memory.c:429-436). Only takes effect on an
                          // actual bit change, and re-evaluates both cores' CMD condition live --
                          // see Sega32X.Interrupts.cs's UpdateCmdIrq remarks.
        {
            byte oldBits = (byte)(Regs[1] & 0x3);
            byte newBits = (byte)(value & 0x3);
            if (oldBits != newBits)
            {
                WriteRegByte(3, newBits);
                UpdateCmdIrq(0);
                UpdateCmdIrq(1);
            }

            return;
        }

        WriteRegByte(offset, value);
    }

    /// <summary>SH-2-side byte read of the adapter/control block (same $4000-$403F window in SH-2
    /// address space — see <see cref="Sega32XSh2Bus"/>). Offset 0's <see cref="NCartBit"/> is
    /// never set (GenesisSharp always has a <see cref="Cartridge"/> — see that field's remarks for
    /// why this is *not* an OR-in the way it might look like it should be), matching the 68k/SH-2
    /// read-view duality PicoDrive's own <c>p32x_sh2reg_read16</c> implements
    /// (<c>memory.c:753-755</c>).</summary>
    internal byte ReadControlByteForSh2(uint offset, bool isSlave)
    {
        if (offset > 0x3F)
        {
            return 0;
        }

        if (offset >= 0x30)
        {
            return ReadPwmByte(offset);
        }

        if (offset == 0)
        {
            return (byte)((Regs[0] >> 8) & 0xFF);
        }

        if (offset == 1) // The SH-2's own per-core IRQ-mask register -- must read back what this
                          // core itself last wrote via WriteRegisterByteFromSh2 (Sh2IrqMask[core]),
                          // *not* whatever ReadRegByte(1) would return (the 68000's own nRES/ADEN
                          // view of this same shared byte offset -- a real, previously-unnoticed
                          // bug: falling through to that generic path meant a real SH-2 read-
                          // modify-write of its own mask register (read current value, OR in a
                          // newly-enabled bit, write back) would read back nRES/ADEN garbage
                          // instead of its actual mask, corrupting it on write-back. Confirmed
                          // against PicoDrive's own p32x_sh2reg_read16 (memory.c:752-755), which
                          // explicitly ORs sh2irq_mask[core] into this same word -- this project's
                          // "bit 0x80" (HEN-ish) companion bit that word also carries isn't tracked
                          // here (see AdvanceHIntCountdown's remarks), so this returns just the
                          // 4-bit mask, not a bit-exact replica of that whole byte.
        {
            return Sh2IrqMask[isSlave ? 1 : 0];
        }

        if (offset == 4) // H count register's high byte -- always reads 0 (real hardware only
                          // ever stores a byte's worth of value; nothing sets bits above it).
        {
            return 0;
        }

        if (offset == 5) // H count register, SH-2-exclusive -- see the write side's remarks.
        {
            return _hIntCounterReg;
        }

        return ReadRegByte(offset);
    }

    /// <summary>SH-2-side byte write of the adapter/control block. Offset 0 (FM/REN) is
    /// 68000-exclusive on real hardware — nothing in PicoDrive's SH-2-side register-write
    /// handlers touches that byte — so writes there are silently ignored. Offset 1 is *not*
    /// 68000-exclusive, despite meaning nRES/ADEN on that side: from the SH-2's own perspective
    /// the same byte offset is its own per-core interrupt-enable register (confirmed
    /// memory.c:824-839 — see <see cref="Sh2IrqMask"/>'s remarks), a real asymmetry this method
    /// used to get wrong by discarding SH-2 writes there entirely. Offsets 0x14/0x16/0x18/0x1a/
    /// 0x1c are the pending-interrupt-clear block (VRES/VINT/HINT/CMD/PWM respectively) — CMD
    /// (0x1a) and VINT (0x16, a documented no-op — see the offset-0x16 case's own remarks) are
    /// wired up so far, the other three clear silently as plain storage until their own phases
    /// land. Every other offset (notably COMM0-7 at 0x20-0x2f) is plain shared storage,
    /// written directly — the same array the 68000 side reads, giving the two CPUs symmetric
    /// read/write access with no extra plumbing needed.</summary>
    internal void WriteRegisterByteFromSh2(uint offset, byte value, bool isSlave)
    {
        if (offset > 0x3F)
        {
            return;
        }

        int core = isSlave ? 1 : 0;

        if (offset == 0) // FM, and *only* FM -- this is how an SH-2 takes ownership of the VDP
                          // register block, the palette, and the frame buffer before touching any
                          // of them, so dropping this write silently disarms every VDP write that
                          // core makes afterwards. Confirmed against PicoDrive's own
                          // p32x_sh2reg_write8 (memory.c, "case 0x00: // FM"):
                          //     r[0] &= ~P32XS_FM;
                          //     r[0] |= (d << 8) & P32XS_FM;
                          // Every other bit of this word (REN, nRES, ADEN, and the read-only
                          // nCART) stays 68000-owned, hence the single-bit merge rather than a
                          // whole-byte store.
                          //
                          // This method previously discarded offset 0 outright. Cost, confirmed on
                          // a real title (Pitfall: The Mayan Adventure): its master SH-2 sets FM,
                          // then writes FBCR's FS bit to aim the next draw at the other frame-buffer
                          // bank, then writes that bank's line table. With FM stuck at 0 the FS
                          // write was swallowed by the (correct) FM gate in
                          // WriteVdpControlByteFromSh2, so both of the game's two line-table passes
                          // landed in the same bank. The other bank kept an all-zero line table
                          // forever while still receiving pixel data, and since the game flips FS
                          // every frame, the display alternated between the finished picture and a
                          // bank that could not resolve a single scanline -- a hard 30Hz flicker.
        {
            Regs[0] = (ushort)((Regs[0] & ~FmBit) | ((value << 8) & FmBit));
            return;
        }

        if (offset == 1)
        {
            bool hadHIntBefore = (Sh2IrqMask[core] & HIntMaskBit) != 0;
            Sh2IrqMask[core] = (byte)(value & 0x0F);
            UpdateCmdIrq(core);
            if (!hadHIntBefore && (Sh2IrqMask[core] & HIntMaskBit) != 0)
            {
                // A core that just unmasked HINT gets a fresh countdown rather than possibly
                // firing on the very next scanline off of however far a *different* core's
                // earlier unmask left the shared countdown -- see AdvanceHIntCountdown's remarks
                // on why the countdown itself is shared, not per-core.
                _hIntCountdown = _hIntCounterReg;
            }

            return;
        }

        if (offset == 4) // "H count" register's high byte -- confirmed ignored on real hardware
                          // (memory.c:840-841, "case 0x04: // ignored?" -- ground truth, not a
                          // guess). Only offset 5 (the low byte) actually holds a value.
        {
            return;
        }

        if (offset == 5) // H count register, SH-2-exclusive -- deliberately *not* routed to the
                          // shared Regs[] word offset 5 already occupies (the 68000's own ROM-bank
                          // select register, ReadRomBankWindowByte) -- offset 5 means a completely
                          // different thing depending on which CPU is asking, the same dual-meaning
                          // shape as offset 1's nRES/ADEN-vs-IRQ-mask split. Confirmed against
                          // PicoDrive's own p32x_sh2reg_write8 (memory.c:842-851, "case 0x05: // H
                          // count", writing Pico32x.sh2_regs[4/2] -- a separate array from the main
                          // adapter regs[] block the 68000-side ROM-bank write targets). See
                          // AdvanceHIntCountdown's remarks for how this value is used.
        {
            _hIntCounterReg = value;
            return;
        }

        if ((offset & ~1u) == 0x1A) // CMD ack -- see AcknowledgeCmdIrq's remarks on why both
                                    // bytes of this word-pair are treated as the same register.
        {
            AcknowledgeCmdIrq(core);
            return;
        }

        // The rest of the per-core interrupt-clear block. These are how a handler stops its
        // interrupt re-firing: the five 32X sources are level-triggered, so servicing one never
        // clears it (see Sh2.SetInterruptRequestLevel) -- only this does. Confirmed against
        // PicoDrive's own p32x_sh2reg_write16 (memory.c:936-952), which clears exactly these four
        // bits at exactly these four offsets, per core, and routes 0x1a differently (above).
        // Word-pair masking matches AcknowledgeCmdIrq's, and for the same reason: real SH-2 code
        // writes these with a word write, which Sega32XSh2Bus decomposes into two byte writes.
        if ((offset & ~1u) == 0x14)
        {
            ClearPendingInterrupt(core, VResPendingBit);
            return;
        }

        if ((offset & ~1u) == 0x16)
        {
            ClearPendingInterrupt(core, VIntPendingBit);
            return;
        }

        if ((offset & ~1u) == 0x18)
        {
            ClearPendingInterrupt(core, HIntPendingBit);
            return;
        }

        if ((offset & ~1u) == 0x1C)
        {
            ClearPendingInterrupt(core, PwmPendingBit);
            return;
        }

        if ((offset & ~1u) == 0x16) // VINT ack. On real hardware this clears a separate pending-
                                     // interrupt bitmask (PicoDrive's own sh2irqi); this core has
                                     // no equivalent for VINT (see OnVerticalBlankStarted's own
                                     // remarks -- Sh2.RaiseInterrupt already self-clears once
                                     // serviced, with no persistent "still requested" register
                                     // state the way CMD's request bit is), so real SH-2 code
                                     // writing here as its interrupt handler's first action is a
                                     // correct, harmless no-op rather than something silently
                                     // falling through to plain storage.
        {
            return;
        }

        if (offset >= 0x30)
        {
            WritePwmByte(offset, value, fromSh2: true);
            return;
        }

        WriteRegByte(offset, value);
    }

    /// <summary>The cartridge ROM as seen by the SH-2s' CS1 window — unbanked, linear, reading
    /// directly from the same image the 68000 sees (GenesisSharp already holds the whole ROM in
    /// memory, unlike PicoDrive's chunked/bank-switched model, so no banking is needed here for
    /// this phase — see this class's known-gaps remarks).</summary>
    internal byte[] CartridgeRom => _cartridge.Rom;

    /// <summary>68000-side byte read of the ROM banking window ($900000-$9FFFFF, see
    /// <c>GenesisConsole.Is32XRomBankWindow</c>) — a selectable 1MB slice of the same cartridge
    /// ROM already visible at $000000-$3FFFFF, bank chosen by the adapter register block's byte
    /// offset 5 (confirmed against PicoDrive's <c>case 0x05: // bank</c>, masked to 2 bits there —
    /// <c>memory.c:439-444</c> — giving up to 4 selectable 1MB banks, matching the Genesis's 4MB
    /// cartridge ceiling). Unlike PicoDrive's own remapped-host-pointer implementation, this reads
    /// straight out of <see cref="_cartridge"/>'s already-fully-resident image — no separate
    /// bank-switch step needed, the same "no chunking, GenesisSharp already holds it all" reasoning
    /// <see cref="CartridgeRom"/> already documents for the SH-2 side. Out-of-bounds (a bank
    /// selection reaching past the ROM's actual size) reads as open bus, matching how plain
    /// cartridge ROM reads already behave past the ROM's own end.</summary>
    public byte ReadRomBankWindowByte(uint offset)
    {
        int bank = ReadRegByte(5) & 0x03;
        uint romOffset = (uint)(bank << 20) + (offset & 0xFFFFF);
        return romOffset < (uint)_cartridge.Rom.Length ? _cartridge.Rom[romOffset] : (byte)0xFF;
    }

    /// <summary>68000-side byte read of the unbanked ROM mirror ($880000-$8FFFFF, see
    /// <c>GenesisConsole.Is32XRomMirrorWindow</c>) — always cartridge ROM starting from offset 0,
    /// completely ignoring the bank-select register <see cref="ReadRomBankWindowByte"/> honors.
    /// Confirmed against PicoDrive's own <c>PicoMemSetup32x</c> ("32X ROM (unbanked...)",
    /// <c>memory.c:2367-2372</c>), which maps <c>Pico.rom</c> directly here with no bank offset.
    /// Same open-bus-past-the-end behavior as the banked window, same reasoning.</summary>
    public byte ReadRomMirrorWindowByte(uint offset) =>
        offset < (uint)_cartridge.Rom.Length ? _cartridge.Rom[offset] : (byte)0xFF;

    private byte ReadRegByte(uint offset)
    {
        ushort word = Regs[offset >> 1];
        return (offset & 1) == 0 ? (byte)(word >> 8) : (byte)word;
    }

    private void WriteRegByte(uint offset, byte value)
    {
        int index = (int)(offset >> 1);
        ushort word = Regs[index];
        Regs[index] = (offset & 1) == 0
            ? (ushort)((word & 0x00FF) | (value << 8))
            : (ushort)((word & 0xFF00) | value);
    }
}
