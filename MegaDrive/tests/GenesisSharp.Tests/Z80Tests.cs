using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

public class Z80Tests
{
    private static (Z80 Cpu, FlatZ80Bus Bus) CreateCpu()
    {
        var bus = new FlatZ80Bus();
        return (new Z80(bus), bus);
    }

    [Fact]
    public void RegisterPairs_CombineHighAndLowBytes()
    {
        var (cpu, _) = CreateCpu();
        cpu.BC = 0x1234;
        cpu.AF = 0xABCD;

        Assert.Equal(0x12, cpu.B);
        Assert.Equal(0x34, cpu.C);
        Assert.Equal(0xAB, cpu.A);
        Assert.Equal(0xCD, cpu.F);
    }

    [Fact]
    public void Add_SetsHalfCarryOverflowAndCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x7F;
        cpu.B = 0x01;
        bus.WriteByte(0, Z80Asm.AluR(0, 0)); // ADD A,B

        cpu.Step();

        Assert.Equal(0x80, cpu.A);
        Assert.True(cpu.FlagSign);
        Assert.False(cpu.FlagZero);
        Assert.True(cpu.FlagParityOverflow); // 0x7F + 0x01 overflows into negative
        Assert.False(cpu.FlagCarry);
        Assert.True(cpu.FlagHalfCarry);
    }

    [Fact]
    public void Sub_SetsBorrowFlags()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x00;
        cpu.B = 0x01;
        bus.WriteByte(0, Z80Asm.AluR(2, 0)); // SUB B

        cpu.Step();

        Assert.Equal(0xFF, cpu.A);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagHalfCarry);
        Assert.True(cpu.FlagSubtract);
    }

    [Fact]
    public void And_AlwaysSetsHalfCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0xF0;
        cpu.B = 0x0F;
        bus.WriteByte(0, Z80Asm.AluR(4, 0)); // AND B

        cpu.Step();

        Assert.Equal(0x00, cpu.A);
        Assert.True(cpu.FlagZero);
        Assert.True(cpu.FlagHalfCarry);
        Assert.True(cpu.FlagParityOverflow); // even parity of 0x00
    }

    [Fact]
    public void Cp_DoesNotModifyA()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 5;
        cpu.B = 5;
        bus.WriteByte(0, Z80Asm.AluR(7, 0)); // CP B

        cpu.Step();

        Assert.Equal(5, cpu.A);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void IncR_SetsOverflowAtSignedBoundary_AndNeverTouchesCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x7F;
        cpu.FlagCarry = true; // sentinel — INC must not touch C
        bus.WriteByte(0, Z80Asm.IncR(0)); // INC B

        cpu.Step();

        Assert.Equal(0x80, cpu.B);
        Assert.True(cpu.FlagParityOverflow);
        Assert.True(cpu.FlagCarry);
    }

    [Fact]
    public void DecR_SetsOverflowAtSignedBoundary_AndNeverTouchesCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x80;
        cpu.FlagCarry = false;
        bus.WriteByte(0, Z80Asm.DecR(0)); // DEC B

        cpu.Step();

        Assert.Equal(0x7F, cpu.B);
        Assert.True(cpu.FlagParityOverflow);
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void LoadRegisterToRegister_ViaHl_ReadsAndWritesMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0x3000;
        bus.WriteByte(0x3000, 0x42);
        bus.WriteByte(0, Z80Asm.LdRR(0, 6)); // LD B,(HL)

        int cycles = cpu.Step();

        Assert.Equal(0x42, cpu.B);
        Assert.Equal(7, cycles);
    }

    [Fact]
    public void AddHlRp_OnlyAffectsHalfCarryAndCarry_NotSignZeroOrParity()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0xFFFF;
        cpu.BC = 0x0001;
        cpu.FlagSign = true; // sentinel — ADD HL,rr must not touch S/Z/PV
        cpu.FlagZero = false;
        bus.WriteByte(0, Z80Asm.AddHlRp(0)); // ADD HL,BC

        cpu.Step();

        Assert.Equal(0x0000, cpu.HL);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagSign); // untouched
        Assert.False(cpu.FlagZero); // untouched
    }

    [Fact]
    public void AdcHlRp_AffectsEveryFlag_UnlikePlainAddHlRp()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0xFFFF;
        cpu.BC = 0x0001;
        cpu.FlagCarry = false;
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, Z80Asm.EdAdcHlRp(0)); // ADC HL,BC

        cpu.Step();

        Assert.Equal(0x0000, cpu.HL);
        Assert.True(cpu.FlagZero);
        Assert.True(cpu.FlagCarry);
    }

    [Fact]
    public void JpCc_TakesTheJumpOnlyWhenConditionHolds()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.FlagZero = false;
        bus.WriteByte(0, Z80Asm.JpCc(0)); // JP NZ,nn
        bus.WriteWord(1, 0x4000);

        cpu.Step();

        Assert.Equal(0x4000, cpu.PC);
    }

    [Fact]
    public void JpCc_FallsThroughWhenConditionFails()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.FlagZero = true;
        bus.WriteByte(0, Z80Asm.JpCc(0)); // JP NZ,nn
        bus.WriteWord(1, 0x4000);

        cpu.Step();

        Assert.Equal(3, cpu.PC); // opcode + 2-byte address, no jump taken
    }

    [Fact]
    public void JrCc_UsesSignedDisplacementRelativeToNextInstruction()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x0100;
        cpu.FlagZero = true;
        bus.WriteByte(0x0100, Z80Asm.JrCc(1)); // JR Z,e
        bus.WriteByte(0x0101, unchecked((byte)(-5)));

        int cycles = cpu.Step();

        Assert.Equal(0x00FD, cpu.PC); // (0x0102) - 5
        Assert.Equal(12, cycles);
    }

    [Fact]
    public void Djnz_DecrementsBAndBranchesUntilZero()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x0100;
        cpu.B = 2;
        bus.WriteByte(0x0100, 0x10); // DJNZ
        bus.WriteByte(0x0101, unchecked((byte)(-2))); // loop back to 0x0100

        int firstCycles = cpu.Step();
        Assert.Equal(1, cpu.B);
        Assert.Equal(0x0100u, cpu.PC);
        Assert.Equal(13, firstCycles);

        int secondCycles = cpu.Step();
        Assert.Equal(0, cpu.B);
        Assert.Equal(0x0102u, cpu.PC); // falls through once B hits zero
        Assert.Equal(8, secondCycles);
    }

    [Fact]
    public void Call_Ret_RoundTripsThroughTheStack()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x0100;
        cpu.SP = 0x2000;
        bus.WriteByte(0x0100, 0xCD); // CALL nn
        bus.WriteWord(0x0101, 0x0200);
        bus.WriteByte(0x0200, 0xC9); // RET

        cpu.Step();
        Assert.Equal(0x0200u, cpu.PC);
        Assert.Equal(0x1FFEu, cpu.SP);

        cpu.Step();
        Assert.Equal(0x0103u, cpu.PC);
        Assert.Equal(0x2000u, cpu.SP);
    }

    [Fact]
    public void CallCc_NotTaken_DoesNotPushReturnAddress()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x0100;
        cpu.SP = 0x2000;
        cpu.FlagZero = false;
        bus.WriteByte(0x0100, Z80Asm.CallCc(1)); // CALL Z,nn
        bus.WriteWord(0x0101, 0x0200);

        int cycles = cpu.Step();

        Assert.Equal(0x0103u, cpu.PC);
        Assert.Equal(0x2000u, cpu.SP);
        Assert.Equal(10, cycles);
    }

    [Fact]
    public void Rst_PushesReturnAddressAndJumpsToFixedVector()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x0100;
        cpu.SP = 0x2000;
        bus.WriteByte(0x0100, Z80Asm.Rst(5)); // RST 28h

        cpu.Step();

        Assert.Equal(0x0028u, cpu.PC);
        Assert.Equal(0x0101, bus.ReadByte(0x1FFE) | (bus.ReadByte(0x1FFF) << 8));
    }

    [Fact]
    public void PushPop_RoundTripsAllFourPairsIncludingAf()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.SP = 0x2000;
        cpu.BC = 0x1111;
        cpu.DE = 0x2222;
        cpu.HL = 0x3333;
        cpu.AF = 0x4444;
        bus.WriteByte(0, Z80Asm.Push(0));
        bus.WriteByte(1, Z80Asm.Push(1));
        bus.WriteByte(2, Z80Asm.Push(2));
        bus.WriteByte(3, Z80Asm.Push(3));
        bus.WriteByte(4, Z80Asm.Pop(0)); // pops what PUSH AF pushed, into BC
        bus.WriteByte(5, Z80Asm.Pop(1));
        bus.WriteByte(6, Z80Asm.Pop(2));
        bus.WriteByte(7, Z80Asm.Pop(3));

        for (int i = 0; i < 8; i++) cpu.Step();

        Assert.Equal(0x4444, cpu.BC);
        Assert.Equal(0x3333, cpu.DE);
        Assert.Equal(0x2222, cpu.HL);
        Assert.Equal(0x1111, cpu.AF);
        Assert.Equal(0x2000u, cpu.SP);
    }

    [Fact]
    public void ExDeHl_SwapsBothPairs()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.DE = 0x1234;
        cpu.HL = 0x5678;
        bus.WriteByte(0, 0xEB); // EX DE,HL

        cpu.Step();

        Assert.Equal(0x5678, cpu.DE);
        Assert.Equal(0x1234, cpu.HL);
    }

    [Fact]
    public void Exx_SwapsWithShadowRegisters()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.BC = 0x1111;
        cpu.AltB = 0x22; cpu.AltC = 0x22;
        bus.WriteByte(0, 0xD9); // EXX

        cpu.Step();

        Assert.Equal(0x2222, cpu.BC);
    }

    [Fact]
    public void Rlca_RotatesCircularlyWithoutTouchingSignZeroOrParity()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x80;
        cpu.FlagZero = true; // sentinel — RLCA must not touch this
        bus.WriteByte(0, 0x07); // RLCA

        cpu.Step();

        Assert.Equal(0x01, cpu.A);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagZero); // untouched
    }

    [Fact]
    public void Rla_UsesOldCarryAsTheIncomingBit_UnlikeRlca()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x00;
        cpu.FlagCarry = true;
        bus.WriteByte(0, 0x17); // RLA

        cpu.Step();

        Assert.Equal(0x01, cpu.A);
        Assert.False(cpu.FlagCarry); // old bit 7 (0) shifted out
    }

    [Fact]
    public void Daa_AfterBcdAddition_CorrectsToPackedDecimal()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x09;
        cpu.B = 0x01;
        bus.WriteByte(0, Z80Asm.AluR(0, 0)); // ADD A,B
        bus.WriteByte(1, 0x27); // DAA

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x10, cpu.A); // 09 + 01 = 10 in BCD
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void Daa_AfterBcdSubtraction_CorrectsToPackedDecimal()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x10;
        cpu.B = 0x01;
        bus.WriteByte(0, Z80Asm.AluR(2, 0)); // SUB B
        bus.WriteByte(1, 0x27); // DAA

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x09, cpu.A); // 10 - 01 = 09 in BCD
    }

    [Fact]
    public void Halt_RepeatsUntilExternallyCleared()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        bus.WriteByte(0, 0x76); // HALT

        int cycles = cpu.Step();
        Assert.True(cpu.Halted);
        Assert.Equal(4, cycles);

        int againCycles = cpu.Step();
        Assert.Equal(4, againCycles);
        Assert.Equal(1u, cpu.PC); // PC only advanced past the HALT opcode itself
    }

    [Fact]
    public void UnassignedOpcode_ThrowsWithOpcodeInMessage()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0xFF); // no such ED opcode implemented

        var ex = Assert.Throws<NotImplementedException>(() => cpu.Step());
        Assert.Contains("FF", ex.Message);
    }
}
