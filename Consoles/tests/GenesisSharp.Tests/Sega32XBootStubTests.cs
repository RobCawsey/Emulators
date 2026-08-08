using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Phase 6 of the in-progress 32X extension (see ARCHITECTURE.md §4a and
/// Sega32X.BootStub.cs): proves the synthesized boot stub actually gets a real 32X-header-shaped
/// ROM running on both SH-2s end to end -- master waits, writes "M_OK" to COMM0:1, and jumps to
/// the entry point named at ROM offset $3E0; slave waits for that "M_OK", writes "S_OK" to
/// COMM2:3, and jumps to the entry point at $3E4 -- entirely through the same
/// <see cref="Sega32X.BootRomMaster"/>/<see cref="Sega32X.BootRomSlave"/> content a real,
/// unmodified 32X ROM would hit (no test-injected boot code, unlike
/// GenesisConsoleSh2BusIntegrationTests.cs's Phase 2 coverage). Also covers the nCART-polarity fix
/// this stub depends on: with the bug (bit always set, backwards for "no cartridge"), both cores
/// would take the dead-end Sega-CD "_CD_" wait branch instead of ever reaching the code below.</summary>
public class Sega32XBootStubTests
{
    private const uint AdapterBase68k = 0xA15100;

    private static void WriteLong(byte[] rom, int offset, uint value)
    {
        rom[offset] = (byte)(value >> 24);
        rom[offset + 1] = (byte)(value >> 16);
        rom[offset + 2] = (byte)(value >> 8);
        rom[offset + 3] = (byte)value;
    }

    private static void WriteSh2Word(byte[] rom, int offset, ushort value) => WriteWord(rom, offset, value);

    private static void WriteWord(byte[] rom, int offset, ushort value)
    {
        rom[offset] = (byte)(value >> 8);
        rom[offset + 1] = (byte)value;
    }

    /// <summary>A ROM with just enough 32X-header shape for the boot stub to do something real:
    /// entry-point pointers at $3E0 (master) / $3E4 (slave), and two tiny hand-assembled SH-2
    /// programs at those addresses proving each core actually reached and ran them -- each writes
    /// a distinctive value into a COMM register the boot stub itself never touches (COMM5/COMM6;
    /// the boot handshake owns COMM0:3), then self-loops.</summary>
    private static Cartridge CreateRomWithSh2EntryPoints()
    {
        var rom = new byte[0x10000];

        const int masterEntryOffset = 0x1000;
        const int slaveEntryOffset = 0x1100;
        WriteLong(rom, 0x3E0, 0x0200_0000u + masterEntryOffset); // master entry pointer (CS1)
        WriteLong(rom, 0x3E4, 0x0200_0000u + slaveEntryOffset);  // slave entry pointer (CS1)

        int pc = masterEntryOffset;
        WriteSh2Word(rom, pc, Sh2Asm.MovI(0x55, n: 0)); pc += 2;   // R0 = 0x55
        WriteSh2Word(rom, pc, Sh2Asm.MovWSG(0x15)); pc += 2;       // MOV.W R0,@(COMM5,GBR)
        int selfLoop = pc;
        WriteSh2Word(rom, pc, Sh2Asm.Bra(-2)); pc += 2;            // self-loop
        WriteSh2Word(rom, pc, Sh2Asm.Nop());

        pc = slaveEntryOffset;
        WriteSh2Word(rom, pc, Sh2Asm.MovI(0x66, n: 0)); pc += 2;   // R0 = 0x66
        WriteSh2Word(rom, pc, Sh2Asm.MovWSG(0x16)); pc += 2;       // MOV.W R0,@(COMM6,GBR)
        WriteSh2Word(rom, pc, Sh2Asm.Bra(-2)); pc += 2;            // self-loop
        WriteSh2Word(rom, pc, Sh2Asm.Nop());

        return Cartridge.LoadFromBin(rom);
    }

    [Fact]
    public void SynthesizedBootStub_RunsBothSh2sIntoTheirRomEntryPoints()
    {
        var console = new GenesisConsole(CreateRomWithSh2EntryPoints());
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(AdapterBase68k + 1, 0x03); // nRES + ADEN

        for (int i = 0; i < 3; i++)
        {
            console.RunFrame();
        }

        Assert.Equal(0x0055, console.Sega32X.Regs[0x15]); // COMM5, written by the master's own code
        Assert.Equal(0x0066, console.Sega32X.Regs[0x16]); // COMM6, written by the slave's own code
        Assert.Equal(0x4D5F, console.Sega32X.Regs[0x10]); // COMM0 high half = "M_"
        Assert.Equal(0x4F4B, console.Sega32X.Regs[0x11]); // COMM1 low half = "OK"
        Assert.Equal(0x535F, console.Sega32X.Regs[0x12]); // COMM2 high half = "S_"
        Assert.Equal(0x4F4B, console.Sega32X.Regs[0x13]); // COMM3 low half = "OK"
    }

    [Fact]
    public void SynthesizedBootStub_SetsBothCoresGbrToTheAdapterRegisterBase()
    {
        var console = new GenesisConsole(CreateRomWithSh2EntryPoints());
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(AdapterBase68k + 1, 0x03);

        Assert.Equal(0x2000_4000u, console.Sega32X.MasterSh2.GBR);
        Assert.Equal(0x2000_4000u, console.Sega32X.SlaveSh2.GBR);
    }

    [Fact]
    public void NCartBit_ReadsClearFromTheSh2SideSinceGenesisSharpAlwaysHasACartridge()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();

        // $4000 is the SH-2-side mirror of the adapter register block's byte 0 (see
        // Sega32XSh2Bus's own CS0 sub-decode) -- going through the public bus, not the internal
        // ReadControlByteForSh2, since GenesisSharp.Tests has no InternalsVisibleTo access.
        byte controlByte0 = console.Sega32X.MasterSh2Bus.ReadByte(0x4000);

        Assert.Equal(0, controlByte0 & 0x01); // nCART bit (SH-2-side byte0, bit0) must be clear
    }

    [Fact]
    public void InitialDataLoad_CopiesTheHeaderNamedBlockFromRomIntoSdram()
    {
        var rom = new byte[0x10000];
        const int srcOffset = 0x2000;
        const int dstOffset = 0x100;
        const int size = 16;
        for (int i = 0; i < size; i++)
        {
            rom[srcOffset + i] = (byte)(0xA0 + i);
        }

        WriteLong(rom, 0x3D4, (uint)srcOffset);
        WriteLong(rom, 0x3D8, (uint)dstOffset);
        WriteLong(rom, 0x3DC, size);

        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        console.Reset();

        for (int i = 0; i < size; i++)
        {
            Assert.Equal((byte)(0xA0 + i), console.Sega32X.Sdram[dstOffset + i]);
        }
    }

    /// <summary>Confirmed against PicoDrive's own no-BIOS fallback (p32x_reset_sh2s, 32x.c:194-195):
    /// "regs[0x28/2] = *(u16 *)(Pico.rom + 0x18e); // checksum and M_OK" -- COMM4 is seeded from
    /// the ROM header's own checksum field, COMM5 is left at zero for the master's own boot code
    /// to set later. A live disassembly trace of a real commercial 32X title (Pitfall: The Mayan
    /// Adventure) confirms the master-side boot code re-derives this same checksum from the ROM
    /// header via the $88xxxx mirror window and compares it against COMM4 alone -- not a 32-bit
    /// COMM4:COMM5 marker, which an earlier revision of this code mistakenly seeded instead.</summary>
    [Fact]
    public void SynthesizedBootStub_SeedsComm4WithTheRomHeaderChecksum()
    {
        byte[] rom = new byte[0x10000];
        WriteWord(rom, 0x18E, 0xBEEF);

        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        console.Reset();

        Assert.Equal(0xBEEF, console.Sega32X.Regs[0x14]); // COMM4 = ROM header checksum
        Assert.Equal(0x0000, console.Sega32X.Regs[0x15]); // COMM5 = left for the program to set
    }

    /// <summary>The 68000-side ROM banking window ($900000-$9FFFFF) -- a real, previously
    /// unimplemented gap this exact real-ROM investigation uncovered: a 68000 jump into this
    /// range with no decode logic behind it read open bus (all-$FFFF, an illegal 68000 opcode)
    /// forever, which is what a real 32X title's own boot code hit when it banked in a slice of
    /// its own ROM to read from. Confirmed against PicoDrive's <c>bank_switch_rom_68k</c>
    /// (<c>memory.c:1449-1484</c>) and its bank-select write dispatch (<c>case 0x05: // bank</c>,
    /// <c>memory.c:439-444</c>, masked to 2 bits there).</summary>
    [Fact]
    public void RomBankWindow_ReturnsTheSelectedOneMegabyteSliceOfCartridgeRom()
    {
        var rom = new byte[0x300000]; // 3MB, so bank 2 has real, distinct content
        rom[0x000000] = 0x11; // bank 0's first byte
        rom[0x100000] = 0x22; // bank 1's first byte
        rom[0x200000] = 0x33; // bank 2's first byte

        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        Assert.Equal(0x11, bus.ReadByte(0x900000)); // bank 0 selected by default (power-on)

        bus.WriteByte(0xA15105, 0x01); // select bank 1
        Assert.Equal(0x22, bus.ReadByte(0x900000));

        bus.WriteByte(0xA15105, 0x02); // select bank 2
        Assert.Equal(0x33, bus.ReadByte(0x900000));

        bus.WriteByte(0x900004, 0xFF); // writes through this window are dropped, like plain ROM
        Assert.Equal(rom[0x200004], bus.ReadByte(0x900004));
    }

    /// <summary>Regression coverage for the third real gap this exact real-ROM investigation
    /// found (after the boot stub and the banked ROM window): a real title's boot code jumps
    /// through $880000 right after releasing the SH-2s -- a *separate*, unbanked mirror of
    /// cartridge ROM that always shows offset 0 regardless of whatever bank $900000 currently has
    /// selected. With it unmapped, that jump read open bus and the whole boot sequence looped
    /// back to the start (repeatedly re-clearing VRAM/CRAM/VSRAM, never actually releasing the
    /// SH-2s), directly observed via GenesisSharp's own debug window on a real, legally-owned
    /// Pitfall 32X dump. Confirmed against PicoDrive's own PicoMemSetup32x ("32X ROM
    /// (unbanked...)", memory.c:2367-2372).</summary>
    [Fact]
    public void RomMirrorWindow_AlwaysReturnsOffsetZeroRegardlessOfTheSelectedBank()
    {
        var rom = new byte[0x300000];
        rom[0x000000] = 0x11;
        rom[0x0006BC] = 0x77; // the exact offset the real ROM investigation jumped through
        rom[0x100000] = 0x22;

        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        Assert.Equal(0x77, bus.ReadByte(0x8806BC));

        bus.WriteByte(0xA15105, 0x01); // select bank 1 on the *banked* window -- must not affect this one
        Assert.Equal(0x77, bus.ReadByte(0x8806BC));
        Assert.Equal(0x11, bus.ReadByte(0x880000));

        bus.WriteByte(0x880004, 0xFF); // writes through this window are dropped, like plain ROM
        Assert.Equal(rom[0x000004], bus.ReadByte(0x880004));
    }

    /// <summary>Regression coverage for a real starvation bug this exact real-ROM investigation
    /// found: <c>RunScanline</c> used to check <c>Sega32X.Sh2sReleased</c> only once, *after* the
    /// 68000 had already run its entire scanline's worth of cycles -- so a 68000 boot-code loop
    /// that toggles nRES/ADEN off again within that same scanline's budget (confirmed happening
    /// in a real, commercial 32X title) could starve both SH-2s of every single cycle, forever,
    /// since the release condition was always false again by the one moment it got checked.
    ///
    /// Uses a trivial, test-injected SH-2 program (directly in <see cref="Sega32X.BootRomMaster"/>,
    /// the same style as GenesisConsoleSh2BusIntegrationTests.cs's Phase 2 coverage) rather than
    /// the real synthesized boot stub, specifically to isolate the interleaving fix from the boot
    /// stub's own separate, deliberate ~131,000-cycle startup delay (PicoDrive's own
    /// "wait a bit... to avoid races with game SH2 code" comment) -- that delay is real, intended
    /// behavior, not something a 68000 that keeps re-resetting the SH-2 before it elapses could
    /// ever be expected to survive, on real hardware or here.</summary>
    [Fact]
    public void Sh2sStillMakeProgress_WhenThe68000TogglesNResAdenWithinASingleScanline()
    {
        var rom = new byte[0x10000];
        WriteLong(rom, 0x000000, 0x00FF8000); // 68000 initial SP
        WriteLong(rom, 0x000004, 0x00000400); // 68000 initial PC

        // 68000 program: toggle nRES+ADEN on, then immediately off, several times in a tight loop
        // -- entirely within a single scanline's ~488-cycle 68000 budget (each MOVE.B pair here
        // costs roughly 40 cycles, so this whole loop repeats many times before one scanline even
        // elapses). Under the old once-per-scanline check, nRES/ADEN being cleared again by the
        // time that single checkpoint arrived meant the SH-2 got zero cycles, ever.
        int loop = 0x400;
        int at = loop;
        WriteWord(rom, at, Asm.MoveImmediate(Size.Byte, dstMode: 7, dstReg: 1)); at += 2;
        WriteWord(rom, at, 0x0003); at += 2; // #3 (nRES + ADEN) -- creates the release edge
        WriteLong(rom, at, 0xA15101); at += 4;

        WriteWord(rom, at, Asm.MoveImmediate(Size.Byte, dstMode: 7, dstReg: 1)); at += 2;
        WriteWord(rom, at, 0x0002); at += 2; // #2 (nRES only, ADEN cleared) -- Sh2sReleased false again
        WriteLong(rom, at, 0xA15101); at += 4;

        WriteWord(rom, at, Asm.Bra((sbyte)(loop - (at + 2))));

        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));

        // SH-2 program: trivial, no delay loop of its own -- builds COMM5's address (@$402A) the
        // same shift/or way GenesisConsoleSh2BusIntegrationTests.cs's Phase 2 program does, so
        // this test doesn't depend on GBR being pre-set by the boot-stub synthesis either.
        var bootRom = console.Sega32X.BootRomMaster;
        WriteLong(bootRom, 0x000000, 0x00000200); // initial PC
        WriteLong(bootRom, 0x000004, 0x06001000); // initial SP (SDRAM)
        int pc = 0x200;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x2A, n: 2)); pc += 2; // COMM5 offset
        WriteSh2Word(bootRom, pc, Sh2Asm.Or(m: 2, n: 1)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x55, n: 0)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovWS(m: 0, n: 1)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.Bra(-2)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());

        console.Reset();

        for (int i = 0; i < 3; i++)
        {
            console.RunFrame();
        }

        Assert.True(0x0055 == console.Sega32X.Regs[0x15],
            $"COMM5={console.Sega32X.Regs[0x15]:X4} MasterPC={console.Sega32X.MasterSh2.PC:X8} " +
            $"MasterCycles={console.Sega32X.MasterSh2.TotalCycles} nRES={console.Sega32X.NRes} ADEN={console.Sega32X.Aden}");
    }

    /// <summary>Regression coverage for a real bug this exact real-ROM investigation found: a
    /// commercial 32X title's own boot code polls <c>REN</c> (bit 7 of the nRES/ADEN byte,
    /// $A15101) and hangs forever if it never reads back set. GenesisSharp's write handler for
    /// that byte used to do a naive full-byte overwrite -- so any ordinary write to set nRES/ADEN
    /// (e.g. writing $03) silently clobbered REN back to 0 in the same operation, even though
    /// REN and nRES/ADEN are otherwise unrelated bits. Confirmed against PicoDrive's own
    /// <c>p32x_reg_write8</c> (<c>memory.c:406-426</c>, its own comment: "writable bits tested"):
    /// only nRES/ADEN (bits 1/0) are ever actually stored from a write to this byte -- REN
    /// survives completely untouched.</summary>
    [Fact]
    public void RenBit_SurvivesWritesToTheNResAdenByteUnchanged()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        Assert.Equal(0x80, bus.ReadByte(AdapterBase68k + 1) & 0x80); // REN set at power-on

        bus.WriteByte(AdapterBase68k + 1, 0x03); // nRES + ADEN -- must not touch REN
        Assert.Equal(0x80, bus.ReadByte(AdapterBase68k + 1) & 0x80);
        Assert.Equal(0x03, bus.ReadByte(AdapterBase68k + 1) & 0x03);

        bus.WriteByte(AdapterBase68k + 1, 0x00); // clear nRES/ADEN -- REN still must not move
        Assert.Equal(0x80, bus.ReadByte(AdapterBase68k + 1) & 0x80);
    }

    /// <summary>Clearing ADEN (a 1->0 transition) forces nRES back to released in the stored
    /// value, confirmed against PicoDrive's own <c>d |= P32XS_nRES</c> in the same handler --
    /// disabling the subsystem leaves the SH-2s not-held but the whole subsystem inert, the same
    /// shape as the power-on default.</summary>
    [Fact]
    public void ClearingAden_ForcesNResBackToReleased()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        bus.WriteByte(AdapterBase68k + 1, 0x01); // ADEN only, nRES held
        Assert.False(console.Sega32X.NRes);

        bus.WriteByte(AdapterBase68k + 1, 0x00); // ADEN 1->0
        Assert.True(console.Sega32X.NRes);
        Assert.False(console.Sega32X.Aden);
    }
}
