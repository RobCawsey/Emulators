using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Phase 2 of the in-progress 32X extension (see ARCHITECTURE.md §4a): proves the bus
/// wiring between a real, stepping SH-2 and a real, stepping 68000 works end-to-end, entirely
/// through hand-assembled code on both sides — no real ROM, BIOS, or graphics needed. Mirrors
/// GenesisConsoleZ80BusArbitrationTests.cs's style.</summary>
public class GenesisConsoleSh2BusIntegrationTests
{
    private const uint Comm0Address68k = 0xA15120;
    private const ushort SentinelValue = 0x0034;

    /// <summary>68000 program: polls COMM0 until the SH-2 has written <see cref="SentinelValue"/>
    /// into it, then stores that value into work RAM so the test can observe it, then loops
    /// forever. Hand-assembled with the existing Asm.cs helpers, same convention as every other
    /// 68000 unit test in this project.</summary>
    private static Cartridge Create68kPollingRom()
    {
        var rom = new byte[0x10000];

        WriteLong(rom, 0x000000, 0x00FF8000); // initial SP
        WriteLong(rom, 0x000004, 0x00000400); // initial PC

        const int loop = 0x400;
        WriteWord(rom, loop, Asm.Move(Size.Word, srcMode: 7, srcReg: 1, dstMode: 0, dstReg: 0)); // MOVE.W (COMM0).L,D0
        WriteLong(rom, loop + 2, Comm0Address68k);
        WriteWord(rom, loop + 6, Asm.Cmp(Size.Word, dn: 0, eaMode: 7, eaReg: 4)); // CMP.W #SentinelValue,D0
        WriteWord(rom, loop + 8, SentinelValue);
        int bne = loop + 10;
        WriteWord(rom, bne, Asm.Bcc(Condition.NotEqual, (sbyte)(loop - (bne + 2)))); // BNE loop

        int store = bne + 2;
        WriteWord(rom, store, Asm.Move(Size.Word, srcMode: 0, srcReg: 0, dstMode: 7, dstReg: 1)); // MOVE.W D0,(workRam).L
        WriteLong(rom, store + 2, 0x00FF0000);

        int halt = store + 6;
        WriteWord(rom, halt, Asm.Bra((sbyte)(halt - (halt + 2)))); // BRA halt (self-loop)

        return Cartridge.LoadFromBin(rom);
    }

    /// <summary>SH-2 program (written into the master's boot ROM): builds the address of COMM0
    /// ($00004020 in SH-2 space — the adapter-register block's $4000 base plus COMM0's 0x20
    /// offset), stores <see cref="SentinelValue"/> there, then loops forever. Hand-assembled with
    /// Sh2Asm.cs, same convention as every SH-2 unit test from Phase 1.
    ///
    /// The self-loop displacement is derived from Sh2.ControlFlow.cs's own confirmed formula
    /// (<c>target = PC_after_fetch + disp*2 + 2</c>, where PC_after_fetch = braAddr+2): for
    /// target == braAddr, disp = -2.</summary>
    private static void WriteSh2Program(byte[] bootRom)
    {
        WriteLong(bootRom, 0x000000, 0x00000200); // initial PC
        WriteLong(bootRom, 0x000004, 0x06001000); // initial SP (inside SDRAM)

        int pc = 0x200;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;      // R1 = 0x00000040
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;           // R1 = 0x00004000
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x20, n: 2)); pc += 2;      // R2 = 0x00000020
        WriteSh2Word(bootRom, pc, Sh2Asm.Or(m: 2, n: 1)); pc += 2;        // R1 |= R2 -> 0x00004020 (COMM0 address)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI((int)SentinelValue, n: 0)); pc += 2; // R0 = SentinelValue
        WriteSh2Word(bootRom, pc, Sh2Asm.MovWS(m: 0, n: 1)); pc += 2;     // MOV.W R0,@R1

        int braAddr = pc;
        WriteSh2Word(bootRom, pc, Sh2Asm.Bra(disp12: -2)); pc += 2;       // self-loop
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());                          // delay slot
    }

    private static void WriteWord(byte[] rom, int offset, ushort value)
    {
        rom[offset] = (byte)(value >> 8);
        rom[offset + 1] = (byte)value;
    }

    private static void WriteLong(byte[] rom, int offset, uint value)
    {
        WriteWord(rom, offset, (ushort)(value >> 16));
        WriteWord(rom, offset + 2, (ushort)value);
    }

    private static void WriteSh2Word(byte[] bootRom, int offset, ushort value) => WriteWord(bootRom, offset, value);

    [Fact]
    public void MasterSh2CanWriteAComm0RegisterThe68000ThenReadsBack()
    {
        var console = new GenesisConsole(Create68kPollingRom());
        WriteSh2Program(console.Sega32X.BootRomMaster);
        console.Reset();

        var bus = (Cpu68000.IBus)console;
        bus.WriteByte(0xA15101, 0x03); // set nRES (already set) + ADEN -> releases both SH-2s

        console.RunFrame();

        Assert.Equal(SentinelValue, console.Sega32X.Regs[0x10]); // COMM0, written by the SH-2
        Assert.Equal(SentinelValue, bus.ReadWord(0x00FF0000)); // confirmed by the 68000's own read-back
    }

    [Fact]
    public void Sh2sStayHeldAndNeverAccrueCyclesUntilReleased()
    {
        var console = new GenesisConsole(Create68kPollingRom());
        WriteSh2Program(console.Sega32X.BootRomMaster);
        console.Reset();

        console.RunFrame();

        Assert.Equal(0, console.Sega32X.MasterSh2.TotalCycles);
        Assert.Equal(0, console.Sega32X.SlaveSh2.TotalCycles);
        Assert.Equal(0, console.Sega32X.Regs[0x10]); // COMM0 never written -- SH-2 never ran
    }

    /// <summary>SH-2 program that takes FM ownership of the 32X VDP registers and then flips
    /// FBCR's <c>FS</c> bit -- the exact two-step a real title performs before drawing into the
    /// other frame-buffer bank. Writes FM (adapter-register block offset 0, at SH-2 $4000) only
    /// when <paramref name="claimFm"/> is set, so the same program doubles as the negative control
    /// proving the FM gate still rejects an unclaimed write.</summary>
    private static void WriteSh2FrameSelectProgram(byte[] bootRom, bool claimFm)
    {
        WriteLong(bootRom, 0x000000, 0x00000200); // initial PC
        WriteLong(bootRom, 0x000004, 0x06001000); // initial SP (inside SDRAM)

        int pc = 0x200;
        if (claimFm)
        {
            WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;  // R1 = 0x00000040
            WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;       // R1 = 0x00004000 (adapter regs)
            WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x80, n: 0)); pc += 2;  // R0 = FM bit (byte offset 0, bit 7)
            WriteSh2Word(bootRom, pc, Sh2Asm.MovBS(m: 0, n: 1)); pc += 2; // MOV.B R0,@R1
        }

        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x41, n: 1)); pc += 2;      // R1 = 0x00000041
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;           // R1 = 0x00004100 (32X VDP regs)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x0B, n: 2)); pc += 2;      // R2 = 0x0B (FBCR low byte)
        WriteSh2Word(bootRom, pc, Sh2Asm.Or(m: 2, n: 1)); pc += 2;        // R1 = 0x0000410B
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x01, n: 0)); pc += 2;      // R0 = FS = 1
        WriteSh2Word(bootRom, pc, Sh2Asm.MovBS(m: 0, n: 1)); pc += 2;     // MOV.B R0,@R1

        WriteSh2Word(bootRom, pc, Sh2Asm.Bra(disp12: -2)); pc += 2;       // self-loop
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());                          // delay slot
    }

    /// <summary>An SH-2 owns the 32X VDP register block by writing FM itself -- the adapter
    /// block's offset 0 is not 68000-exclusive. Regression test: this method used to discard SH-2
    /// writes to offset 0 outright, which left FM stuck at 0 and made the FM gate in
    /// <c>WriteVdpControlByteFromSh2</c> silently swallow every subsequent VDP register write that
    /// core made -- see <c>Sega32X.WriteRegisterByteFromSh2</c>'s offset-0 remarks for what that
    /// cost on a real title.</summary>
    [Fact]
    public void MasterSh2ClaimingFmCanThenFlipTheFrameSelectBit()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        WriteSh2FrameSelectProgram(console.Sega32X.BootRomMaster, claimFm: true);
        console.Reset();

        var bus = (Cpu68000.IBus)console;
        bus.WriteByte(0xA15101, 0x03); // nRES + ADEN -> release both SH-2s

        console.RunFrame();

        Assert.NotEqual(0, console.Sega32X.Regs[0] & 0x8000); // FM claimed by the SH-2
        Assert.NotEqual(0, console.Sega32X.VdpRegs[5] & 0x1); // ...so its FS write actually landed
    }

    /// <summary>The other half of the same rule: without claiming FM first, the SH-2's VDP
    /// register writes really are dropped (the 68000 still owns the block). Guards against
    /// "fixing" the above by removing the ownership gate altogether.</summary>
    [Fact]
    public void MasterSh2WithoutClaimingFmCannotFlipTheFrameSelectBit()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        WriteSh2FrameSelectProgram(console.Sega32X.BootRomMaster, claimFm: false);
        console.Reset();

        var bus = (Cpu68000.IBus)console;
        bus.WriteByte(0xA15101, 0x03);

        console.RunFrame();

        Assert.Equal(0, console.Sega32X.Regs[0] & 0x8000); // FM never claimed
        Assert.Equal(0, console.Sega32X.VdpRegs[5] & 0x1); // so FS stays put
    }

    [Fact]
    public void MarsIdRegister_AlwaysReadsTheHardwarePresenceString()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        var bus = (Cpu68000.IBus)console;

        Assert.Equal((byte)'M', bus.ReadByte(0xA130EC));
        Assert.Equal((byte)'A', bus.ReadByte(0xA130ED));
        Assert.Equal((byte)'R', bus.ReadByte(0xA130EE));
        Assert.Equal((byte)'S', bus.ReadByte(0xA130EF));
    }

    [Fact]
    public void ControlRegister_NResEdgeResetsBothSh2sTogether()
    {
        var console = new GenesisConsole(Cartridge.LoadFromBin(new byte[0x10000]));
        console.Reset();
        var bus = (Cpu68000.IBus)console;

        console.Sega32X.MasterSh2.R[0] = 0x1234;
        console.Sega32X.SlaveSh2.R[0] = 0x5678;

        bus.WriteByte(0xA15101, 0x00); // clear nRES -- hold in reset
        bus.WriteByte(0xA15101, 0x02); // 0 -> 1 edge on nRES alone (ADEN stays clear)

        Assert.Equal(0u, console.Sega32X.MasterSh2.R[0]);
        Assert.Equal(0u, console.Sega32X.SlaveSh2.R[0]);
    }
}
