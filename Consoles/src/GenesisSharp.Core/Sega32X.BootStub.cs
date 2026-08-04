namespace GenesisSharp.Core;

/// <summary>Phase 6 of the in-progress 32X extension — a synthesized replacement for the real 32X
/// BIOS, since GenesisSharp doesn't ship one (Sega's own firmware, not something this project can
/// legally include). Without *something* here, both SH-2s reset to a blank <see
/// cref="Sega32X.BootRomMaster"/>/<see cref="Sega32X.BootRomSlave"/> (all-zero bytes), decode
/// opcode 0x0000 as illegal, and spin forever on the illegal-instruction trap at address 0 — which
/// is exactly what happens when loading a real, unmodified 32X ROM before this file existed.
///
/// Ground truth: PicoDrive's own no-real-BIOS fallback, <c>get_bios()</c>
/// (<c>reference/PicoDrive/picodrive/pico/32x/memory.c:2195-2294</c>) for the boot-ROM *content*
/// synthesized here, and <c>p32x_reset_sh2s</c> (<c>reference/PicoDrive/picodrive/pico/32x/
/// 32x.c:161-209</c>) for the host-side setup in <see cref="SynthesizeSh2BootStateFromCartridge"/>.
/// Both are themselves already a synthesized fallback in PicoDrive (not a real Sega BIOS dump) —
/// this file is a from-scratch re-encoding of the same instruction sequence and the same host-side
/// register/memory setup, not a literal copy of PicoDrive's C source.
///
/// The boot-ROM machine code below is ported essentially verbatim (opcode-for-opcode) rather than
/// reformulated, precisely because it implements a real cross-CPU handshake protocol (the "M_OK"/
/// "S_OK" comm-register synchronization real 32X software also relies on) where a subtly wrong
/// transcription would produce a boot stub that *looks* plausible but silently deadlocks or races
/// with the two SH-2s — the same "port literally, don't get clever" judgment this project already
/// applies to MAC.L/MAC.W's saturation logic and DIV1's Q/M/T bookkeeping.</summary>
public sealed partial class Sega32X
{
    /// <summary>Master SH-2 boot stub, placed at boot-ROM offset 0x200 (see
    /// <see cref="PopulateSynthesizedBootRoms"/> for what leads up to it). Confirmed
    /// opcode-for-opcode against PicoDrive's <c>msh2_code</c> (<c>memory.c:2103-2160</c>): traps to
    /// itself if anything jumps in before real init (defensive only); waits a beat for the 68000's
    /// own init to finish (to avoid racing a game's SH-2 code, PicoDrive's own comment cites
    /// <em>Tempo</em> as the motivating title); writes "M_OK" into COMM0:COMM1 so the slave core
    /// (spinning on exactly that value — see <see cref="SlaveBootStub"/>) can proceed; then reads
    /// the master entry-point pointer PicoDrive's own comment names "master start pointer in ROM"
    /// — a 32-bit big-endian address stored at cartridge ROM offset $3E0 — and jumps to it. The
    /// <c>cd_start</c> branch (waiting for a Sega CD sub-CPU to write "_CD_" into COMM0) is dead
    /// code for GenesisSharp: it is only reachable when the SH-2-visible "no cartridge" bit is
    /// set, and GenesisSharp always has a <see cref="Cartridge"/> (see <see cref="NCartBit"/>'s
    /// remarks on the polarity bug this depends on being fixed correctly) — included anyway so
    /// this stays a faithful, unmodified port rather than a hand-trimmed subset.</summary>
    private static readonly ushort[] MasterBootStub =
    {
        0xaffe, // 200 bra <self>
        0x0009, // 202 nop
        0xd406, // 204 mov.l   @(_m_ok,pc), r4
        0xc400, // 206 mov.b   @(h'0,gbr),r0
        0xc801, // 208 tst     #1, r0
        0x8b0f, // 20a bf      cd_start
        0xd105, // 20c mov.l   @(_cnt,pc), r1
        0xd206, // 20e mov.l   @(_start,pc), r2
        0x71ff, // 210 add     #-1, r1
        0x4115, // 212 cmp/pl  r1
        0x89fc, // 214 bt      -2
        0x6043, // 216 mov     r4, r0
        0xc208, // 218 mov.l   r0, @(h'20,gbr)
        0x6822, // 21a mov.l   @r2, r8
        0x482b, // 21c jmp     @r8
        0x0009, // 21e nop
        0x4d5f, 0x4f4b, // 220 _m_ok = "M_OK"
        0x0001, 0x0000, // 224 _cnt
        0x2200, 0x03e0, // 228 master start pointer in ROM ($3E0, cache-through CS1)
        0xd20d, // 22c cd_start: mov.l @(__cd_,pc), r2
        0xc608, // 22e mov.l   @(h'20,gbr), r0
        0x3200, // 230 cmp/eq  r0, r2
        0x8bfc, // 232 bf      #-2
        0xe000, // 234 mov     #0, r0
        0xcf80, // 236 or.b    #0x80,@(r0,gbr)
        0xd80b, // 238 mov.l   @(_start_cd,pc), r8
        0xd30c, // 23a mov.l   @(_max_len,pc), r3
        0x5b84, // 23c mov.l   @(h'10,r8), r11
        0x5a82, // 23e mov.l   @(8,r8), r10
        0x5081, // 240 mov.l   @(4,r8), r0
        0x5980, // 242 mov.l   @(0,r8), r9
        0x3036, // 244 cmp/hi  r3,r0
        0x8b00, // 246 bf      #1
        0x6033, // 248 mov     r3,r0
        0x7820, // 24a add     #0x20, r8
        0x6286, // 24c ipl_copy: mov.l @r8+, r2
        0x2922, // 24e mov.l   r2, @r9
        0x7904, // 250 add     #4, r9
        0x70fc, // 252 add     #-4, r0
        0x8800, // 254 cmp/eq  #0, r0
        0x8bf9, // 256 bf      #-5
        0x4b2e, // 258 ldc     r11, vbr
        0x6043, // 25a mov     r4, r0
        0xc208, // 25c mov.l   r0, @(h'20,gbr)
        0x4a2b, // 25e jmp     @r10
        0x0009, // 260 nop
        0x0009, // 262 nop (pad)
        0x5f43, 0x445f, // 264 __cd_ = "_CD_"
        0x2400, 0x0018, // 268 _start_cd
        0x0001, 0xffe0, // 26c _max_len
    };

    /// <summary>Slave SH-2 boot stub. Confirmed opcode-for-opcode against PicoDrive's
    /// <c>ssh2_code</c> (<c>memory.c:2162-2193</c>): spins on COMM0:COMM1 until it reads "M_OK"
    /// (written by <see cref="MasterBootStub"/> above — the real cross-CPU synchronization point),
    /// then writes "S_OK" into COMM2:COMM3, reads the slave entry-point pointer from cartridge ROM
    /// offset $3E4, and jumps there. Same <c>cd_start</c>-is-dead-code note as the master stub.</summary>
    private static readonly ushort[] SlaveBootStub =
    {
        0xaffe, // 200 bra <self>
        0x0009, // 202 nop
        0xd106, // 204 mov.l   @(_m_ok,pc), r1
        0xd208, // 206 mov.l   @(_start,pc), r2
        0xc608, // 208 mov.l   @(h'20,gbr), r0
        0x3100, // 20a cmp/eq  r0, r1
        0x8bfc, // 20c bf      #-2
        0xc400, // 20e mov.b   @(h'0,gbr),r0
        0xc801, // 210 tst     #1, r0
        0xd004, // 212 mov.l   @(_s_ok,pc), r0
        0x8b0a, // 214 bf      cd_start
        0xc209, // 216 mov.l   r0, @(h'24,gbr)
        0x6822, // 218 mov.l   @r2, r8
        0x482b, // 21a jmp     @r8
        0x0009, // 21c nop
        0x0009, // 21e nop (pad)
        0x4d5f, 0x4f4b, // 220 _m_ok = "M_OK"
        0x535f, 0x4f4b, // 224 _s_ok = "S_OK"
        0x2200, 0x03e4, // 228 slave start pointer in ROM ($3E4, cache-through CS1)
        0xd803, // 22c cd_start: mov.l @(_start_cd,pc), r8
        0x5b85, // 22e mov.l   @(h'14,r8), r11
        0x5a83, // 230 mov.l   @(h'0c,r8), r10
        0x4b2e, // 232 ldc     r11, vbr
        0xc209, // 234 mov.l   r0, @(h'24,gbr)
        0x4a2b, // 236 jmp     @r10
        0x0009, // 238 nop
        0x0009, // 23a nop (pad)
        0x2400, 0x0018, // 23c _start_cd
    };

    /// <summary>Populates <see cref="BootRomMaster"/>/<see cref="BootRomSlave"/> with the
    /// synthesized fallback stubs above, exactly once (constructor-time, not on every reset — a
    /// real boot ROM is fixed silicon, and tests that inject their own hand-assembled program via
    /// these same public arrays, per Phase 2's own convention, run *after* construction and simply
    /// overwrite whatever's here). Every exception vector defaults to a trap-to-self at offset
    /// 0x200 except the reset vector (PC/SP, and the master's secondary "manual reset" vector
    /// slot), which points at the real stub entry immediately after it — confirmed against
    /// <c>get_bios()</c>'s own vector-fill loops (<c>memory.c:2249-2293</c>), including the
    /// master-only quirk (PicoDrive's own comment: "CD titles by Digital Pictures jump to 0x140
    /// for resetting") of filling $140-$1FB with NOPs and a BRA over the trap, so that legacy
    /// jump-to-$140 convention still lands safely at the real init code instead of the trap.</summary>
    private void PopulateSynthesizedBootRoms()
    {
        for (int i = 0; i < 80; i++)
        {
            WriteLongAt(BootRomMaster, i * 4, 0x200);
        }

        for (int wordIndex = 0x140 / 2; wordIndex < 0x1FC / 2; wordIndex++)
        {
            WriteWordAt(BootRomMaster, wordIndex * 2, 0x0009); // nop
        }

        WriteWordAt(BootRomMaster, 0x1FC, 0xa002); // bra 0x204
        WriteWordAt(BootRomMaster, 0x1FE, 0x0009); // nop (delay slot)

        WriteLongAt(BootRomMaster, 0x0, 0x204);
        WriteLongAt(BootRomMaster, 0x8, 0x204);
        WriteLongAt(BootRomMaster, 0x4, 0x0604_0000); // reset SP: SDRAM top
        WriteLongAt(BootRomMaster, 0xC, 0x0604_0000);

        CopyStub(MasterBootStub, BootRomMaster, 0x200);

        for (int i = 0; i < 128; i++)
        {
            WriteLongAt(BootRomSlave, i * 4, 0x200);
        }

        WriteLongAt(BootRomSlave, 0x0, 0x204);
        WriteLongAt(BootRomSlave, 0x8, 0x204);
        WriteLongAt(BootRomSlave, 0x4, 0x0603_F800); // reset SP: just below the master's SDRAM stack
        WriteLongAt(BootRomSlave, 0xC, 0x0603_F800);

        CopyStub(SlaveBootStub, BootRomSlave, 0x200);
    }

    private static void CopyStub(ushort[] stub, byte[] destination, int destinationOffset)
    {
        for (int i = 0; i < stub.Length; i++)
        {
            WriteWordAt(destination, destinationOffset + i * 2, stub[i]);
        }
    }

    private static void WriteWordAt(byte[] destination, int offset, ushort value)
    {
        destination[offset] = (byte)(value >> 8);
        destination[offset + 1] = (byte)value;
    }

    private static void WriteLongAt(byte[] destination, int offset, uint value)
    {
        WriteWordAt(destination, offset, (ushort)(value >> 16));
        WriteWordAt(destination, offset + 2, (ushort)value);
    }

    /// <summary>The part of a real 32X BIOS's job that's most naturally expressed as a direct
    /// host-side memory operation rather than as SH-2 machine code: setting each core's <c>GBR</c>
    /// to the adapter-register base (so <see cref="MasterBootStub"/>/<see cref="SlaveBootStub"/>'s
    /// GBR-relative reads/writes actually land on COMM0-3 rather than on the boot ROM itself),
    /// each core's <c>VBR</c> to the value the cartridge's own 32X header specifies, and copying
    /// the header-named "Initial Data Load" block out of cartridge ROM and into SDRAM before either
    /// SH-2 starts running. Confirmed against PicoDrive's <c>p32x_reset_sh2s</c>
    /// (<c>32x.c:161-209</c>), specifically its <c>p32x_bios_m/p32x_bios_s == NULL</c> branches —
    /// always taken in GenesisSharp, since no real BIOS is ever loaded. Runs every time nRES is
    /// asserted (both at power-on, via <see cref="Reset"/>, and at any later 68000-triggered nRES
    /// edge — see <see cref="WriteControlByteFrom68k"/>), matching PicoDrive's own call sites for
    /// <c>p32x_reset_sh2s</c> exactly. Header field offsets ($3D4/$3D8/$3DC for the IDL
    /// source/destination/size, $3E8/$3EC for the master/slave VBR) are the documented 32X ROM
    /// header convention, not values specific to PicoDrive's own implementation. COMM4:5's seed
    /// value is a separate matter — see the remarks at that assignment below, it's speculative,
    /// not header-derived or PicoDrive-confirmed.</summary>
    private void SynthesizeSh2BootStateFromCartridge()
    {
        const uint GbrValue = 0x2000_4000; // cache-through mirror of the adapter-register base $4000
        MasterSh2.GBR = GbrValue;
        SlaveSh2.GBR = GbrValue;

        byte[] rom = _cartridge.Rom;
        if (rom.Length < 0x3F0)
        {
            return; // header too short to carry the 32X boot fields -- nothing more to do
        }

        MasterSh2.VBR = ReadHeaderLong(rom, 0x3E8);
        SlaveSh2.VBR = ReadHeaderLong(rom, 0x3EC);

        uint idlSrc = ReadHeaderLong(rom, 0x3D4) & 0x0FFF_FFFF;
        uint idlDst = ReadHeaderLong(rom, 0x3D8) & 0x0FFF_FFFF;
        uint idlSize = ReadHeaderLong(rom, 0x3DC);
        for (uint i = 0; i < idlSize && idlSrc + i < (uint)rom.Length && idlDst + i < (uint)Sdram.Length; i++)
        {
            Sdram[idlDst + i] = rom[idlSrc + i];
        }

        // Confirmed against PicoDrive's own no-BIOS fallback (p32x_reset_sh2s, 32x.c:194-195):
        // "Pico32x.regs[0x28/2] = *(u16 *)(Pico.rom + 0x18e); // checksum and M_OK" -- COMM4 is
        // seeded from the ROM header's own checksum field ($18E), and COMM5 is deliberately left
        // untouched (zero, from Reset) with PicoDrive's own comment noting "program will set
        // M_OK" -- i.e. the master's own boot code is expected to overwrite COMM4 with an M_OK
        // marker itself once it validates this checksum, not something the BIOS/boot-stub seeds.
        // A live disassembly trace of a real, commercial 32X title (Pitfall: The Mayan Adventure)
        // confirms this end-to-end: its master-side boot code spin-waits on COMM4 alone (word
        // read, offset 40 from the adapter-register base) being nonzero, then compares it against
        // a value it loads from the ROM header's own checksum field via the $88xxxx mirror window
        // -- i.e. the *game itself* re-derives the same checksum this seeds and compares against
        // it, exactly matching PicoDrive's "checksum" comment. (An earlier revision of this code
        // seeded COMM4:5 with the ASCII marker "SLAV" instead, misreading a later step of that
        // same trace; reverted once the checksum-comparison code came into view.)
        Regs[0x14] = ReadHeaderWord(rom, 0x18E); // COMM4 = ROM header checksum
        Regs[0x15] = 0;                          // COMM5 = left for the program itself to set
    }

    private static uint ReadHeaderLong(byte[] rom, int offset) =>
        ((uint)rom[offset] << 24) | ((uint)rom[offset + 1] << 16) | ((uint)rom[offset + 2] << 8) | rom[offset + 3];

    private static ushort ReadHeaderWord(byte[] rom, int offset) =>
        (ushort)(((uint)rom[offset] << 8) | rom[offset + 1]);
}
