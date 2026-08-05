using GenesisSharp.Core;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

/// <summary>Phases 1-2 of the in-progress 32X interrupt-routing extension (see
/// <c>32X-INTERRUPT-ROUTING-PLAN.md</c>): the CMD interrupt, VINT, and the SH-2's own per-core
/// interrupt-enable register (<c>Sega32X.Interrupts.cs</c>).</summary>
public class Sega32XInterruptTests
{
    private static Sega32X CreateSega32X()
    {
        var sega32X = new Sega32X(Cartridge.LoadFromBin(new byte[0x10000]));
        sega32X.Reset();
        return sega32X;
    }

    /// <summary>Confirms the real asymmetry this phase fixes: adapter-block offset 1 means
    /// nRES/ADEN when the 68000 writes it (unaffected here — same word untouched), but means the
    /// SH-2's own per-core interrupt-enable register when an SH-2 writes it. Before this phase,
    /// GenesisSharp discarded SH-2 writes to this offset entirely, treating it as
    /// 68000-exclusive — confirmed wrong against PicoDrive's own p32x_sh2reg_write8
    /// (memory.c:824-839).</summary>
    [Fact]
    public void WriteRegisterByteFromSh2_OffsetOne_SetsThatCoresIrqMaskWithoutAffectingNResAden()
    {
        var sega32X = CreateSega32X();
        ushort regsWordZeroBeforeWrite = sega32X.Regs[0]; // REN|nRES, ADEN clear (power-on default)

        sega32X.MasterSh2Bus.WriteByte(0x4001, 0x0F); // real SH-2-facing bus write, offset 1

        Assert.Equal(0x0F, sega32X.Sh2IrqMask[0]); // master's own mask register set
        Assert.Equal(0x00, sega32X.Sh2IrqMask[1]); // slave's own mask register untouched
        Assert.Equal(regsWordZeroBeforeWrite, sega32X.Regs[0]); // 68000's nRES/ADEN view unaffected
    }

    /// <summary>The slave core writing offset 1 only ever affects its own mask register, never
    /// the master's — confirmed against PicoDrive's own per-core <c>sh2irq_mask[sh2->is_slave]</c>
    /// indexing (memory.c:825,829).</summary>
    [Fact]
    public void WriteRegisterByteFromSh2_OffsetOne_OnlyAffectsTheWritingCoresOwnMask()
    {
        var sega32X = CreateSega32X();

        sega32X.SlaveSh2Bus.WriteByte(0x4001, 0x05);

        Assert.Equal(0x00, sega32X.Sh2IrqMask[0]);
        Assert.Equal(0x05, sega32X.Sh2IrqMask[1]);
    }

    private const uint AdapterBase68k = 0xA15100;
    private const uint Comm1Address68k = AdapterBase68k + 0x22;
    private const ushort SentinelValue = 0x0055;

    /// <summary>68000 program: releases both SH-2s, requests a CMD interrupt on the master only
    /// (irq-ctl bit 0), then polls COMM1 until the master's own CMD handler has written <see
    /// cref="SentinelValue"/> there, storing the result into work RAM so the test can observe it.
    /// Hand-assembled with Asm.cs, same convention as GenesisConsoleSh2BusIntegrationTests.cs.</summary>
    private static Cartridge Create68kCmdRequestRom()
    {
        var rom = new byte[0x10000];

        WriteLong(rom, 0x000000, 0x00FF8000); // initial SP
        WriteLong(rom, 0x000004, 0x00000400); // initial PC

        int at = 0x400;
        WriteWord(rom, at, Asm.MoveImmediate(Size.Byte, dstMode: 7, dstReg: 1)); at += 2;
        WriteWord(rom, at, 0x0003); at += 2; // #3 (nRES|ADEN) -- releases both SH-2s
        WriteLong(rom, at, AdapterBase68k + 1); at += 4;

        WriteWord(rom, at, Asm.MoveImmediate(Size.Byte, dstMode: 7, dstReg: 1)); at += 2;
        WriteWord(rom, at, 0x0001); at += 2; // request CMD to master only (bit 0)
        WriteLong(rom, at, AdapterBase68k + 3); at += 4;

        int loop = at;
        WriteWord(rom, at, Asm.Move(Size.Word, srcMode: 7, srcReg: 1, dstMode: 0, dstReg: 0)); at += 2; // MOVE.W (COMM1).L,D0
        WriteLong(rom, at, Comm1Address68k); at += 4;
        WriteWord(rom, at, Asm.Cmp(Size.Word, dn: 0, eaMode: 7, eaReg: 4)); at += 2; // CMP.W #SentinelValue,D0
        WriteWord(rom, at, SentinelValue); at += 2;
        int bne = at;
        WriteWord(rom, at, Asm.Bcc(Condition.NotEqual, (sbyte)(loop - (bne + 2)))); at += 2; // BNE loop

        WriteWord(rom, at, Asm.Move(Size.Word, srcMode: 0, srcReg: 0, dstMode: 7, dstReg: 1)); at += 2; // MOVE.W D0,(workRam).L
        WriteLong(rom, at, 0x00FF0000); at += 4;

        int halt = at;
        WriteWord(rom, at, Asm.Bra((sbyte)(halt - (halt + 2)))); // BRA halt (self-loop)

        return Cartridge.LoadFromBin(rom);
    }

    /// <summary>Master SH-2 program (boot ROM): unmasks all interrupt levels, sets its own CMD
    /// mask bit (adapter-block offset 1), then self-loops waiting to be interrupted. The CMD
    /// handler, installed at vector 68 (confirmed level 8 / vector 68 in
    /// <c>32X-INTERRUPT-ROUTING-PLAN.md</c>'s own Ground Truth table), writes <see
    /// cref="SentinelValue"/> to COMM1 and acknowledges via offset 0x1a before returning.
    /// Addresses built the same way GenesisConsoleSh2BusIntegrationTests.cs's own SH-2 program
    /// does (immediate-load + shift + or, no GBR dependency).</summary>
    private static void WriteSh2Program(byte[] bootRom)
    {
        WriteLong(bootRom, 0x000000, 0x00000200); // initial PC
        WriteLong(bootRom, 0x000004, 0x06001000); // initial SP (inside SDRAM)

        const int vector68Offset = 68 * 4; // VBR defaults to 0 on reset (Sh2.Reset), so this is
                                            // also the absolute address of the vector-68 slot.
        const int handlerAddress = 0x300;
        WriteLong(bootRom, vector68Offset, (uint)handlerAddress);

        int pc = 0x200;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0, n: 0)); pc += 2;        // R0 = 0
        WriteSh2Word(bootRom, pc, Sh2Asm.LdcSr(0)); pc += 2;             // SR = 0 -- unmask all levels
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;     // R1 = 0x40
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;          // R1 = 0x4000 (adapter base)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0, n: 2)); pc += 2;        // R2 = 0
        WriteSh2Word(bootRom, pc, Sh2Asm.MovBS(m: 2, n: 1)); pc += 2;    // MOV.B R2,@R1 (offset 0 -- FM, harmless)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(1, n: 3)); pc += 2;        // R3 = 1
        WriteSh2Word(bootRom, pc, Sh2Asm.Add(m: 3, n: 1)); pc += 2;      // R1 = 0x4001 (offset 1)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(2, n: 2)); pc += 2;        // R2 = 2 (CmdMaskBit, bit 1 -- not 1, which is PwmMaskBit)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovBS(m: 2, n: 1)); pc += 2;    // MOV.B R2,@R1 -- Sh2IrqMask[master] = CmdMaskBit

        int braAddr = pc;
        WriteSh2Word(bootRom, pc, Sh2Asm.Bra(disp12: -2)); pc += 2;      // self-loop, waiting to be interrupted
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());                        // delay slot

        pc = handlerAddress;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;     // R1 = 0x40
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;          // R1 = 0x4000
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x22, n: 2)); pc += 2;     // R2 = 0x22 (COMM1 offset)
        WriteSh2Word(bootRom, pc, Sh2Asm.Or(m: 2, n: 1)); pc += 2;       // R1 = 0x4022 (COMM1 address)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI((int)SentinelValue, n: 0)); pc += 2; // R0 = SentinelValue
        WriteSh2Word(bootRom, pc, Sh2Asm.MovWS(m: 0, n: 1)); pc += 2;    // MOV.W R0,@R1 -- write COMM1

        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 3)); pc += 2;     // R3 = 0x40
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 3)); pc += 2;          // R3 = 0x4000
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x1A, n: 4)); pc += 2;     // R4 = 0x1a (CMD ack offset)
        WriteSh2Word(bootRom, pc, Sh2Asm.Or(m: 4, n: 3)); pc += 2;       // R3 = 0x401a
        WriteSh2Word(bootRom, pc, Sh2Asm.MovWS(m: 0, n: 3)); pc += 2;    // MOV.W R0,@R3 -- acknowledge (value irrelevant)

        WriteSh2Word(bootRom, pc, Sh2Asm.Rte()); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());                        // delay slot
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

    /// <summary>End-to-end proof of the whole CMD protocol confirmed in
    /// <c>32X-INTERRUPT-ROUTING-PLAN.md</c>'s Ground Truth section: the 68000 requests a CMD
    /// interrupt via adapter-block offset 3, the master SH-2 (having unmasked CMD via its own
    /// offset-1 write) actually takes a real interrupt at vector 68, services it, writes a
    /// sentinel value back through COMM1, and acknowledges via offset 0x1a — all through real
    /// CPU stepping on both sides, no test-injected shortcuts. Mirrors
    /// GenesisConsoleSh2BusIntegrationTests.cs's own style.</summary>
    [Fact]
    public void CmdInterrupt_68000RequestServicedByMasterSh2_RoundTripsThroughARealInterrupt()
    {
        var console = new GenesisConsole(Create68kCmdRequestRom());
        WriteSh2Program(console.Sega32X.BootRomMaster);
        console.Reset();

        console.RunFrame();

        var bus = (Cpu68000.IBus)console;
        Assert.True(console.Sega32X.MasterSh2.TotalCycles > 0, "master SH-2 never ran"); // sanity: it was actually released and stepped
        Assert.Equal(0x02, console.Sega32X.Sh2IrqMask[0]); // its own CmdMaskBit write took effect
        Assert.Equal(0, console.Sega32X.MasterSh2.PendingInterruptLevel); // consumed by servicing, not still pending
        Assert.Equal(SentinelValue, console.Sega32X.Regs[0x11]); // COMM1, written by the master's CMD handler
        Assert.Equal(SentinelValue, bus.ReadWord(0x00FF0000)); // confirmed by the 68000's own read-back
        Assert.Equal(0, console.Sega32X.Regs[1] & 0x1); // master's own request bit acknowledged (cleared) via offset 0x1a
    }

    /// <summary>The live-AND this phase's whole design hinges on: a CMD request the target core
    /// hasn't unmasked never raises an interrupt at all, confirmed against
    /// <c>Sh2.PendingInterruptLevel</c> staying at its post-reset 0 -- distinguishes "the request
    /// bit got stored" (which it should, plain register storage) from "an actual interrupt got
    /// raised" (which requires the live mask AND, per PicoDrive's own p32x_update_cmd_irq).</summary>
    [Fact]
    public void CmdInterrupt_RequestedButNotUnmaskedByTheTargetCore_NeverFires()
    {
        var sega32X = CreateSega32X();
        Assert.Equal(0, sega32X.MasterSh2.PendingInterruptLevel); // baseline, confirmed before acting

        // Request CMD to master via the real 68000-facing write path, without ever unmasking it
        // (Sh2IrqMask[0] stays 0 -- the power-on default).
        sega32X.WriteControlByteFrom68k(3, 0x01);

        Assert.Equal(0, sega32X.MasterSh2.PendingInterruptLevel); // still 0 -- the request was stored, not delivered
        Assert.Equal(1, sega32X.Regs[1] & 0x1); // the request bit itself IS stored, confirming this isn't silently dropped
    }

    /// <summary>Confirms the hook itself, independent of any real <see cref="Vdp"/> instance --
    /// <see cref="Sega32X.OnVerticalBlankStarted"/> is decoupled via the event pattern
    /// <c>GenesisConsole</c>'s constructor wires up, so it can be exercised directly. Unlike CMD,
    /// there's no request bit to check first -- confirmed against PicoDrive's own
    /// <c>p32x_start_blank</c> (<c>32x.c:316-330</c>), VINT is a one-shot edge raised
    /// unconditionally at vblank-start, gated only by each core's own mask bit.</summary>
    [Fact]
    public void OnVerticalBlankStarted_CoreWithVIntUnmasked_RaisesInterruptOnThatCoreOnly()
    {
        var sega32X = CreateSega32X();
        sega32X.MasterSh2Bus.WriteByte(0x4001, 0x08); // VIntMaskBit (bit 3) on master only

        sega32X.OnVerticalBlankStarted();

        Assert.Equal(12, sega32X.MasterSh2.PendingInterruptLevel);
        Assert.Equal(0, sega32X.SlaveSh2.PendingInterruptLevel); // never unmasked -- never raised
    }

    [Fact]
    public void OnVerticalBlankStarted_NeitherCoreHasVIntUnmasked_NeverFires()
    {
        var sega32X = CreateSega32X(); // Sh2IrqMask stays all-zero, the power-on default

        sega32X.OnVerticalBlankStarted();

        Assert.Equal(0, sega32X.MasterSh2.PendingInterruptLevel);
        Assert.Equal(0, sega32X.SlaveSh2.PendingInterruptLevel);
    }

    private const uint Comm2Address68k = AdapterBase68k + 0x24;
    private const ushort VIntSentinelValue = 0x0077;

    /// <summary>End-to-end proof that a real vblank edge -- not a hand-called method -- actually
    /// reaches the SH-2 as a real interrupt: releases both SH-2s, lets a single real
    /// <c>RunFrame()</c> reach its own natural vblank (no test-injected timing), and confirms the
    /// master's own VINT handler (vector 70, confirmed in <c>32X-INTERRUPT-ROUTING-PLAN.md</c>'s
    /// Ground Truth table) actually ran by observing its sentinel write through COMM2. Mirrors the
    /// CMD round-trip test's shape and the same real-CPU-stepping standard.</summary>
    [Fact]
    public void VInt_ARealFramesOwnVBlankEdge_RoundTripsThroughARealInterruptOnTheMaster()
    {
        var rom = new byte[0x10000];
        WriteLong(rom, 0x000000, 0x00FF8000); // initial SP
        WriteLong(rom, 0x000004, 0x00000400); // initial PC
        int at = 0x400;
        WriteWord(rom, at, Asm.MoveImmediate(Size.Byte, dstMode: 7, dstReg: 1)); at += 2;
        WriteWord(rom, at, 0x0003); at += 2; // #3 (nRES|ADEN) -- releases both SH-2s
        WriteLong(rom, at, AdapterBase68k + 1); at += 4;
        WriteWord(rom, at, Asm.Bra((sbyte)(at - (at + 2)))); // self-loop, 68000 side is a bystander here
        var console = new GenesisConsole(Cartridge.LoadFromBin(rom));

        var bootRom = console.Sega32X.BootRomMaster;
        WriteLong(bootRom, 0x000000, 0x00000200); // initial PC
        WriteLong(bootRom, 0x000004, 0x06001000); // initial SP (inside SDRAM)

        const int vector70Offset = 70 * 4; // VBR defaults to 0 on reset, so also the absolute address
        const int handlerAddress = 0x300;
        WriteLong(bootRom, vector70Offset, (uint)handlerAddress);

        int pc = 0x200;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0, n: 0)); pc += 2;        // R0 = 0
        WriteSh2Word(bootRom, pc, Sh2Asm.LdcSr(0)); pc += 2;             // SR = 0 -- unmask all levels
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;     // R1 = 0x40
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;          // R1 = 0x4000
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(1, n: 3)); pc += 2;        // R3 = 1
        WriteSh2Word(bootRom, pc, Sh2Asm.Add(m: 3, n: 1)); pc += 2;      // R1 = 0x4001 (offset 1)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(8, n: 2)); pc += 2;        // R2 = 8 (VIntMaskBit, bit 3)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovBS(m: 2, n: 1)); pc += 2;    // MOV.B R2,@R1 -- Sh2IrqMask[master] = VIntMaskBit

        WriteSh2Word(bootRom, pc, Sh2Asm.Bra(disp12: -2)); pc += 2;      // self-loop, waiting for vblank
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());

        pc = handlerAddress;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x40, n: 1)); pc += 2;     // R1 = 0x40
        WriteSh2Word(bootRom, pc, Sh2Asm.Shll8(n: 1)); pc += 2;          // R1 = 0x4000
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI(0x24, n: 2)); pc += 2;     // R2 = 0x24 (COMM2 offset)
        WriteSh2Word(bootRom, pc, Sh2Asm.Or(m: 2, n: 1)); pc += 2;       // R1 = 0x4024 (COMM2 address)
        WriteSh2Word(bootRom, pc, Sh2Asm.MovI((int)VIntSentinelValue, n: 0)); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.MovWS(m: 0, n: 1)); pc += 2;    // MOV.W R0,@R1 -- write COMM2
        WriteSh2Word(bootRom, pc, Sh2Asm.Rte()); pc += 2;
        WriteSh2Word(bootRom, pc, Sh2Asm.Nop());

        console.Reset();
        console.RunFrame();

        var bus = (Cpu68000.IBus)console;
        Assert.True(console.Sega32X.MasterSh2.TotalCycles > 0, "master SH-2 never ran");
        Assert.Equal(VIntSentinelValue, console.Sega32X.Regs[0x12]); // COMM2, written by the VINT handler
        Assert.Equal(VIntSentinelValue, bus.ReadWord(Comm2Address68k));
    }
}
