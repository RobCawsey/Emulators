using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

public class Z80CbEdTests
{
    private static (Z80 Cpu, FlatZ80Bus Bus) CreateCpu()
    {
        var bus = new FlatZ80Bus();
        return (new Z80(bus), bus);
    }

    [Fact]
    public void Rlc_UnlikeRlca_SetsSignZeroAndParity()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x80;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbRotate(0, 0)); // RLC B

        int cycles = cpu.Step();

        Assert.Equal(0x01, cpu.B);
        Assert.True(cpu.FlagCarry);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagSign);
        Assert.Equal(8, cycles);
    }

    [Fact]
    public void Sra_PreservesTheSignBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x81;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbRotate(5, 0)); // SRA B

        cpu.Step();

        Assert.Equal(0xC0, cpu.B); // sign bit copied down, old bit0 shifted into carry
        Assert.True(cpu.FlagCarry);
    }

    [Fact]
    public void Srl_ClearsTheTopBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x81;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbRotate(7, 0)); // SRL B

        cpu.Step();

        Assert.Equal(0x40, cpu.B);
        Assert.True(cpu.FlagCarry);
    }

    [Fact]
    public void Sll_UndocumentedShiftAlwaysFillsBitZeroWithOne()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x00;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbRotate(6, 0)); // SLL B

        cpu.Step();

        Assert.Equal(0x01, cpu.B);
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void RotateOnIndirectHl_CostsMoreCyclesThanOnARegister()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0x3000;
        bus.WriteByte(0x3000, 0x01);
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbRotate(0, 6)); // RLC (HL)

        int cycles = cpu.Step();

        Assert.Equal(0x02, bus.ReadByte(0x3000));
        Assert.Equal(15, cycles);
    }

    [Fact]
    public void Bit_SetsZeroWhenBitIsClear_AndNeverModifiesTheOperand()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0b0000_0100;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbBit(2, 0)); // BIT 2,B

        int cycles = cpu.Step();

        Assert.False(cpu.FlagZero);
        Assert.True(cpu.FlagHalfCarry);
        Assert.Equal(0b0000_0100, cpu.B);
        Assert.Equal(8, cycles);
    }

    [Fact]
    public void Bit_ClearBit_SetsZeroFlag()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbBit(2, 0));

        cpu.Step();

        Assert.True(cpu.FlagZero);
        Assert.True(cpu.FlagParityOverflow); // PV mirrors Z for BIT
    }

    [Fact]
    public void Res_ClearsOnlyTheTargetBit_WithoutAffectingFlags()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0xFF;
        cpu.FlagZero = true; // sentinel — RES must not touch flags
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbRes(3, 0)); // RES 3,B

        cpu.Step();

        Assert.Equal(0xF7, cpu.B);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Set_SetsOnlyTheTargetBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 0x00;
        bus.WriteByte(0, 0xCB);
        bus.WriteByte(1, Z80Asm.CbSet(3, 0)); // SET 3,B

        cpu.Step();

        Assert.Equal(0x08, cpu.B);
    }

    [Fact]
    public void Ldi_CopiesByteAndAdvancesBothPointers()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0x3000;
        cpu.DE = 0x4000;
        cpu.BC = 2;
        bus.WriteByte(0x3000, 0xAB);
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0xA0); // LDI

        int cycles = cpu.Step();

        Assert.Equal(0xAB, bus.ReadByte(0x4000));
        Assert.Equal(0x3001, cpu.HL);
        Assert.Equal(0x4001, cpu.DE);
        Assert.Equal(1, cpu.BC);
        Assert.True(cpu.FlagParityOverflow); // BC still nonzero
        Assert.Equal(16, cycles);
    }

    [Fact]
    public void Ldir_RepeatsUntilCounterExhausted()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.HL = 0x3000;
        cpu.DE = 0x4000;
        cpu.BC = 3;
        bus.LoadBytes(0x3000, 0x11, 0x22, 0x33);
        bus.WriteByte(0x1000, 0xED);
        bus.WriteByte(0x1001, 0xB0); // LDIR

        int cycles1 = cpu.Step();
        Assert.Equal(0x1000u, cpu.PC); // rewound to repeat
        Assert.Equal(21, cycles1);

        int cycles2 = cpu.Step();
        Assert.Equal(0x1000u, cpu.PC);
        Assert.Equal(21, cycles2);

        int cycles3 = cpu.Step();
        Assert.Equal(0x1002u, cpu.PC); // falls through once BC hits 0
        Assert.Equal(16, cycles3);

        Assert.Equal(0x11, bus.ReadByte(0x4000));
        Assert.Equal(0x22, bus.ReadByte(0x4001));
        Assert.Equal(0x33, bus.ReadByte(0x4002));
        Assert.Equal(0, cpu.BC);
        Assert.False(cpu.FlagParityOverflow);
    }

    [Fact]
    public void Cpir_StopsEarlyOnAMatch()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.HL = 0x3000;
        cpu.BC = 5;
        cpu.A = 0x33;
        bus.LoadBytes(0x3000, 0x11, 0x22, 0x33, 0x44, 0x55);
        bus.WriteByte(0x1000, 0xED);
        bus.WriteByte(0x1001, 0xB1); // CPIR

        cpu.Step(); // no match (0x11), BC=4, repeats
        Assert.Equal(0x1000u, cpu.PC);
        cpu.Step(); // no match (0x22), BC=3, repeats
        Assert.Equal(0x1000u, cpu.PC);
        cpu.Step(); // match (0x33): stop even though BC (2) is still nonzero

        Assert.Equal(0x1002u, cpu.PC);
        Assert.True(cpu.FlagZero);
        Assert.Equal(2, cpu.BC);
        Assert.Equal(0x3003, cpu.HL);
    }

    [Fact]
    public void Neg_NegatesAAndSetsOverflowOnlyAtMinimumValue()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x80;
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x44); // NEG

        cpu.Step();

        Assert.Equal(0x80, cpu.A); // -128 stays -128 in two's complement
        Assert.True(cpu.FlagParityOverflow);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagSubtract);
    }

    [Fact]
    public void LdAI_CopiesInterruptVectorAndReflectsIff2InParity()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.I = 0x42;
        cpu.Iff2 = true;
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x57); // LD A,I

        cpu.Step();

        Assert.Equal(0x42, cpu.A);
        Assert.True(cpu.FlagParityOverflow);
    }

    [Fact]
    public void LdIA_And_LdRA_CopyIntoTheSpecialRegisters()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x77;
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x47); // LD I,A
        bus.WriteByte(2, 0xED);
        bus.WriteByte(3, 0x4F); // LD R,A

        cpu.Step();
        cpu.Step();

        Assert.Equal(0x77, cpu.I);
        Assert.Equal(0x77, cpu.R);
    }

    [Fact]
    public void Retn_RestoresIff1FromIff2()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.SP = 0x2000;
        cpu.Iff1 = false;
        cpu.Iff2 = true;
        bus.WriteWord(0x2000, 0x3000);
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x45); // RETN

        cpu.Step();

        Assert.Equal(0x3000u, cpu.PC);
        Assert.True(cpu.Iff1);
    }

    [Fact]
    public void Im_SetsInterruptMode()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x5E); // IM 2

        cpu.Step();

        Assert.Equal(2, cpu.InterruptMode);
    }

    [Fact]
    public void InRegisterC_ReadsPortAndSetsParity()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.C = 0x10;
        bus.WritePort(0x10, 0x0F); // 0x0F has even parity (4 bits set)
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, Z80Asm.EdInC(0)); // IN B,(C)

        cpu.Step();

        Assert.Equal(0x0F, cpu.B);
        Assert.True(cpu.FlagParityOverflow);
    }

    [Fact]
    public void OutRegisterC_WritesPortWithoutTouchingFlags()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.C = 0x20;
        cpu.B = 0x99;
        cpu.FlagZero = true; // sentinel
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, Z80Asm.EdOutC(0)); // OUT (C),B

        cpu.Step();

        Assert.Equal(0x99, bus.ReadPort(0x20));
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Rld_RotatesNibblesBetweenAAndMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0x3000;
        cpu.A = 0x7A;
        bus.WriteByte(0x3000, 0x31);
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x6D); // RLD

        cpu.Step();

        // low(HL)=1 -> high(HL); old high(HL)=3 -> low(A); old low(A)=0xA -> low(HL).
        Assert.Equal(0x1A, bus.ReadByte(0x3000));
        Assert.Equal(0x73, cpu.A);
    }

    [Fact]
    public void Rrd_RotatesNibblesTheOtherDirection()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0x3000;
        cpu.A = 0x7A;
        bus.WriteByte(0x3000, 0x31);
        bus.WriteByte(0, 0xED);
        bus.WriteByte(1, 0x67); // RRD

        cpu.Step();

        // low(HL)=1 -> low(A); old low(A)=0xA -> high(HL); old high(HL)=3 -> low(HL).
        Assert.Equal(0xA3, bus.ReadByte(0x3000));
        Assert.Equal(0x71, cpu.A);
    }
}
