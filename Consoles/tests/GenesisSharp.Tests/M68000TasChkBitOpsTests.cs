using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000TasChkBitOpsTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Tas_OnDataRegister_SetsBit7AndFlagsFromOriginalValue()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x7F;
        bus.WriteWord(0x1000, Asm.Tas(eaMode: 0, eaReg: 0));

        int cycles = cpu.Step();

        Assert.Equal(0xFFu, cpu.D[0]);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagNegative);
        Assert.Equal(4, cycles);
    }

    [Fact]
    public void Tas_OnZeroValue_SetsZeroFlagBeforeForcingBit7()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x00;
        bus.WriteWord(0x1000, Asm.Tas(eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0x80u, cpu.D[0]);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Tas_OnMemory_ReadsTestsAndWritesBack()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteByte(0x3000, 0x01);
        bus.WriteWord(0x1000, Asm.Tas(eaMode: 2, eaReg: 0)); // TAS (A0)

        int cycles = cpu.Step();

        Assert.Equal(0x81, bus.ReadByte(0x3000));
        Assert.False(cpu.FlagZero);
        Assert.Equal(14, cycles); // 10 + 4 (byte EA via (An))
    }

    [Fact]
    public void Opcode4AFC_TrapsAsIllegalInstruction_RatherThanBeingDecodedAsTasWithImmediate()
    {
        // $4AFC falls inside TAS's general $4AC0-$4AFF opcode range (EA mode 7/reg 4, i.e.
        // "immediate"), but Motorola carved this exact word out as the dedicated illegal-
        // instruction opcode -- TAS on an immediate operand wouldn't make sense anyway, since
        // TAS needs a writable destination. A real ROM (Sonic 1) hit this: our dispatcher used
        // to decode it as TAS, silently consuming a fake immediate word and resuming two words
        // later in the middle of unrelated bytes instead of trapping to vector 4.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000; // supervisor, mask = 0
        cpu.A[7] = 0x3000;
        bus.WriteLong(0x10, 0x9000); // vector 4 (illegal instruction) = address 4*4 = 0x10
        bus.WriteWord(0x1000, 0x4AFC);
        bus.WriteWord(0x1002, 0x1234); // if this were wrongly read as TAS's immediate operand

        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(0x2FFAu, cpu.A[7]); // pushed a long PC + word SR = 6 bytes
        Assert.Equal(0x1002u, bus.ReadLong(0x2FFC)); // return address is right after the illegal opcode word, not past the fake immediate too
        Assert.Equal(34, cycles);
    }

    [Fact]
    public void LineFOpcode_TrapsToVector11_RatherThanThrowing()
    {
        // A real ROM (Sonic 1) executes $FFDC -- a Line F ("Line 1111 Emulator") opcode,
        // originally reserved for 68881/68882 coprocessor instructions the Genesis's plain
        // 68000 never had. Real hardware traps unconditionally for the entire $F000-$FFFF
        // range regardless of the specific bits, rather than crashing.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000; // supervisor, mask = 0
        cpu.A[7] = 0x3000;
        bus.WriteLong(0x2C, 0x9100); // vector 11 (Line F) = address 11*4 = 0x2C
        bus.WriteWord(0x1000, 0xFFDC);

        int cycles = cpu.Step();

        Assert.Equal(0x9100u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(0x1002u, bus.ReadLong(0x2FFC));
        Assert.Equal(34, cycles);
    }

    [Fact]
    public void LineAOpcode_TrapsToVector10_RatherThanThrowing()
    {
        // The entire $A000-$AFFF range was reserved for software-defined "Line 1010 Emulator"
        // traps, never assigned to a real instruction -- same deal as Line F but vector 10.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.A[7] = 0x3000;
        bus.WriteLong(0x28, 0x9200); // vector 10 (Line A) = address 10*4 = 0x28
        bus.WriteWord(0x1000, 0xA000);

        int cycles = cpu.Step();

        Assert.Equal(0x9200u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(0x1002u, bus.ReadLong(0x2FFC));
        Assert.Equal(34, cycles);
    }

    [Fact]
    public void Chk_ValueWithinBounds_FallsThroughWithoutTrapping()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 5;
        cpu.D[1] = 10;
        bus.WriteWord(0x1000, Asm.Chk(reg: 0, eaMode: 0, eaReg: 1)); // CHK D1,D0

        int cycles = cpu.Step();

        Assert.Equal(0x1002u, cpu.PC);
        Assert.Equal(5u, cpu.D[0]);
        Assert.Equal(10, cycles);
    }

    [Fact]
    public void Chk_NegativeValue_TrapsViaVector6AndSetsNegative()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000; // start out of supervisor mode to prove exception entry sets it
        cpu.A[7] = 0x2000;
        cpu.D[0] = 0xFFFF_FFFF; // -1
        cpu.D[1] = 10;
        bus.WriteLong(0x18, 0x9000); // vector 6 (CHK) handler
        bus.WriteWord(0x1000, Asm.Chk(reg: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.True(cpu.FlagNegative);
        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(0x1FFAu, cpu.A[7]); // pushed a long PC + word SR = 6 bytes
        Assert.Equal(0x1002u, bus.ReadLong(0x1FFC)); // return address, below SR on the stack
        Assert.Equal(0x0008, bus.ReadWord(0x1FFA)); // saved SR, on top of the stack — includes the N bit CHK just set
    }

    [Fact]
    public void Chk_ValueAboveBound_TrapsAndClearsNegative()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[7] = 0x2000;
        cpu.D[0] = 15;
        cpu.D[1] = 10;
        bus.WriteLong(0x18, 0x9000);
        bus.WriteWord(0x1000, Asm.Chk(reg: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.False(cpu.FlagNegative);
        Assert.Equal(0x9000u, cpu.PC);
    }

    [Fact]
    public void Btst_Static_TestsBitWithoutModifyingOperand()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0b0000_0100;
        bus.WriteWord(0x1000, Asm.BitOpStatic(opType: 0, eaMode: 0, eaReg: 0));
        bus.WriteWord(0x1002, 2); // bit number

        int cycles = cpu.Step();

        Assert.False(cpu.FlagZero); // bit 2 is set
        Assert.Equal(0b0000_0100u, cpu.D[0]); // BTST never modifies
        Assert.Equal(10, cycles); // 6 (Dn) + 4 (static immediate fetch)
    }

    [Fact]
    public void Btst_Static_ClearBit_SetsZeroFlag()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0;
        bus.WriteWord(0x1000, Asm.BitOpStatic(opType: 0, eaMode: 0, eaReg: 0));
        bus.WriteWord(0x1002, 2);

        cpu.Step();

        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Bchg_Static_OnMemory_TogglesTheBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteByte(0x3000, 0x00);
        bus.WriteWord(0x1000, Asm.BitOpStatic(opType: 1, eaMode: 2, eaReg: 0)); // BCHG #3,(A0)
        bus.WriteWord(0x1002, 3);

        cpu.Step();

        Assert.Equal(0x08, bus.ReadByte(0x3000));
        Assert.True(cpu.FlagZero); // bit was 0 before the change
    }

    [Fact]
    public void Bclr_Dynamic_OnDataRegister_ClearsTheBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xFF;
        cpu.D[5] = 5; // bit number
        bus.WriteWord(0x1000, Asm.BitOpDynamic(opType: 2, bitRegister: 5, eaMode: 0, eaReg: 0)); // BCLR D5,D0

        cpu.Step();

        Assert.Equal(0xDFu, cpu.D[0]);
        Assert.False(cpu.FlagZero); // bit 5 was set before clearing
    }

    [Fact]
    public void Bset_Dynamic_OnDataRegister_WrapsBitNumberModulo32()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0;
        cpu.D[1] = 35; // 35 mod 32 = 3
        bus.WriteWord(0x1000, Asm.BitOpDynamic(opType: 3, bitRegister: 1, eaMode: 0, eaReg: 0)); // BSET D1,D0

        cpu.Step();

        Assert.Equal(0x08u, cpu.D[0]);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Btst_Dynamic_OnMemory_WrapsBitNumberModulo8()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteByte(0x3000, 0x01);
        cpu.D[2] = 8; // 8 mod 8 = 0
        bus.WriteWord(0x1000, Asm.BitOpDynamic(opType: 0, bitRegister: 2, eaMode: 2, eaReg: 0)); // BTST D2,(A0)

        cpu.Step();

        Assert.False(cpu.FlagZero); // bit 0 is set
        Assert.Equal(0x01, bus.ReadByte(0x3000)); // BTST never modifies
    }

    [Fact]
    public void And_RegisterForm_StillDecodesCorrectly_NotMistakenForBitOp()
    {
        // Regression check: dynamic bit-op decode lives in the same top nibble (0000) as
        // ORI/ANDI/etc; make sure a genuine ANDI opcode still reaches ExecuteAndImmediate.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xFF;
        bus.WriteWord(0x1000, 0x0200); // ANDI.B #imm,D0 (opcode only; size=byte, EA=D0)
        bus.WriteWord(0x1002, 0x00F0); // immediate word (low byte 0xF0 used)

        cpu.Step();

        Assert.Equal(0xF0u, cpu.D[0]);
    }
}
