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

    /// <summary>SH-2-visible-only "cartridge present" bit, OR'd into the SH-2 side's read of
    /// Regs[0] (PicoDrive: <c>Pico32x.sh2_regs[0] |= P32XS2_nCART</c>, <c>32x.c:130-132</c>, bit
    /// position <c>P32XS2_nCART = 1&lt;&lt;8</c>, <c>pico_int.h:595</c>). Always true in
    /// GenesisSharp, since <see cref="GenesisConsole"/> always requires a <see cref="Cartridge"/>.</summary>
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
        MasterSh2Bus = new Sega32XSh2Bus(this, isSlave: false);
        SlaveSh2Bus = new Sega32XSh2Bus(this, isSlave: true);
        MasterSh2 = new Sh2(MasterSh2Bus);
        SlaveSh2 = new Sh2(SlaveSh2Bus);
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
        ResetVdp();
        ResetPwm();
        MasterSh2.Reset();
        SlaveSh2.Reset();
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

    /// <summary>68000-side byte write of the adapter/control block. The 0→1 transition of nRES
    /// (offset 1, bit1) resets both SH-2s together (see <see cref="NRes"/>'s remarks) — this is
    /// 68000-exclusive; nothing on the SH-2 side can assert or release its own reset.</summary>
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

        bool wasReset = NRes;
        WriteRegByte(offset, value);
        if (!wasReset && NRes)
        {
            MasterSh2.Reset();
            SlaveSh2.Reset();
        }
    }

    /// <summary>SH-2-side byte read of the adapter/control block (same $4000-$403F window in SH-2
    /// address space — see <see cref="Sega32XSh2Bus"/>). Offset 0 additionally OR's in
    /// <see cref="NCartBit"/>, matching the 68k/SH-2 read-view duality PicoDrive's own
    /// <c>p32x_sh2reg_read16</c> implements (<c>memory.c:753-755</c>).</summary>
    internal byte ReadControlByteForSh2(uint offset)
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
            return (byte)(((Regs[0] | NCartBit) >> 8) & 0xFF);
        }

        return ReadRegByte(offset);
    }

    /// <summary>SH-2-side byte write of the adapter/control block. Offsets 0/1 (FM/REN/nRES/ADEN)
    /// are 68000-exclusive on real hardware — nothing in PicoDrive's SH-2-side register-write
    /// handlers touches those bits — so writes there are silently ignored rather than applied.
    /// Every other offset (notably COMM0-7 at 0x20-0x2f) is plain shared storage, written directly
    /// — the same array the 68000 side reads, giving the two CPUs symmetric read/write access with
    /// no extra plumbing needed.</summary>
    internal void WriteRegisterByteFromSh2(uint offset, byte value)
    {
        if (offset is 0 or 1 || offset > 0x3F)
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
