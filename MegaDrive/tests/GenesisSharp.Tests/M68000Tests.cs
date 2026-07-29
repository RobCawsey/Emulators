using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000Tests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Reset_LoadsInitialSspAndPcFromVectorTable()
    {
        var (cpu, bus) = CreateCpu();
        bus.WriteLong(0x000000, 0x0000_2000);
        bus.WriteLong(0x000004, 0x0000_1000);

        cpu.Reset();

        Assert.Equal(0x0000_2000u, cpu.A[7]);
        Assert.Equal(0x0000_1000u, cpu.PC);
        Assert.Equal(0x2700, cpu.SR);
    }

    [Theory]
    [InlineData(5, 5u, false, false)]
    [InlineData(0, 0u, false, true)]
    [InlineData(-1, 0xFFFF_FFFFu, true, false)]
    public void MoveQuick_SignExtendsAndSetsFlags(int data, uint expected, bool expectNegative, bool expectZero)
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        bus.WriteWord(0x1000, Asm.MoveQuick(reg: 2, data));

        int cycles = cpu.Step();

        Assert.Equal(expected, cpu.D[2]);
        Assert.Equal(expectNegative, cpu.FlagNegative);
        Assert.Equal(expectZero, cpu.FlagZero);
        Assert.False(cpu.FlagOverflow);
        Assert.False(cpu.FlagCarry);
        Assert.Equal(4, cycles);
        Assert.Equal(0x1002u, cpu.PC);
    }

    [Fact]
    public void MoveImmediateLong_WritesValueAndAdvancesPastExtensionWords()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        bus.WriteWord(0x1000, Asm.MoveImmediate(Size.Long, dstMode: 0, dstReg: 3));
        bus.WriteLong(0x1002, 0x1234_5678);

        cpu.Step();

        Assert.Equal(0x1234_5678u, cpu.D[3]);
        Assert.Equal(0x1006u, cpu.PC);
        Assert.False(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
    }

    [Fact]
    public void Move_ToAddressRegisterDestination_DoesNotAffectFlags()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.FlagZero = true; // sentinel — MOVEA must leave CCR alone
        cpu.D[0] = 0;
        bus.WriteWord(0x1000, Asm.Move(Size.Long, srcMode: 0, srcReg: 0, dstMode: 1, dstReg: 5));

        cpu.Step();

        Assert.Equal(0u, cpu.A[5]);
        Assert.True(cpu.FlagZero, "MOVEA must not touch CCR.");
    }

    [Fact]
    public void Lea_ComputesAddressIntoAddressRegister()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[1] = 0x3000;
        bus.WriteWord(0x1000, Asm.Lea(dstAddressRegister: 0, srcMode: 2, srcReg: 1)); // LEA (A1),A0

        cpu.Step();

        Assert.Equal(0x3000u, cpu.A[0]);
    }

    [Fact]
    public void Add_Word_SetsOverflowWithoutCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x7FFF;
        cpu.D[1] = 0x0001;
        bus.WriteWord(0x1000, Asm.AddDnPlusEa(Size.Word, dn: 0, eaMode: 0, eaReg: 1)); // D0 += D1

        cpu.Step();

        Assert.Equal(0x8000u, cpu.D[0]);
        Assert.True(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.True(cpu.FlagOverflow);
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void Add_Word_SetsCarryAndExtendOnWrap()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xFFFF;
        cpu.D[1] = 0x0001;
        bus.WriteWord(0x1000, Asm.AddDnPlusEa(Size.Word, dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(0u, cpu.D[0] & 0xFFFF);
        Assert.True(cpu.FlagZero);
        Assert.False(cpu.FlagOverflow);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagExtend);
    }

    [Fact]
    public void Sub_Word_SetsCarryOnBorrow()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x0000;
        cpu.D[1] = 0x0001;
        bus.WriteWord(0x1000, Asm.SubDnMinusEa(Size.Word, dn: 0, eaMode: 0, eaReg: 1)); // D0 -= D1

        cpu.Step();

        Assert.Equal(0xFFFFu, cpu.D[0] & 0xFFFF);
        Assert.True(cpu.FlagNegative);
        Assert.True(cpu.FlagCarry);
        Assert.False(cpu.FlagOverflow);
    }

    [Fact]
    public void Sub_Word_SetsOverflowWithoutCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x8000; // minimum negative word
        cpu.D[1] = 0x0001;
        bus.WriteWord(0x1000, Asm.SubDnMinusEa(Size.Word, dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(0x7FFFu, cpu.D[0] & 0xFFFF);
        Assert.True(cpu.FlagOverflow);
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void Cmp_DoesNotModifyEitherOperand()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 5;
        cpu.D[1] = 5;
        bus.WriteWord(0x1000, Asm.Cmp(Size.Word, dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(5u, cpu.D[0]);
        Assert.Equal(5u, cpu.D[1]);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void And_ComputesBitwiseAndIntoDataRegisterOperand()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xF0F0;
        cpu.D[1] = 0x0FF0;
        bus.WriteWord(0x1000, Asm.AndDnAndEa(Size.Word, dn: 0, eaMode: 0, eaReg: 1)); // D0 &= D1

        cpu.Step();

        Assert.Equal(0x00F0u, cpu.D[0]);
    }

    [Fact]
    public void Or_WritesResultIntoEaOperand()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0x0F0F;
        cpu.D[2] = 0xFF00;
        bus.WriteWord(0x1000, Asm.OrEaOrDn(Size.Word, dn: 1, eaMode: 0, eaReg: 2)); // D2 |= D1

        cpu.Step();

        Assert.Equal(0xFF0Fu, cpu.D[2]);
        Assert.Equal(0x0F0Fu, cpu.D[1]);
    }

    [Fact]
    public void Eor_WritesResultIntoEaOperand()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[3] = 0xAAAA;
        cpu.D[4] = 0x5555;
        bus.WriteWord(0x1000, Asm.Eor(Size.Word, dn: 3, eaMode: 0, eaReg: 4)); // D4 ^= D3

        cpu.Step();

        Assert.Equal(0xFFFFu, cpu.D[4]);
        Assert.Equal(0xAAAAu, cpu.D[3]);
    }

    [Fact]
    public void Bcc_BranchesWhenConditionIsTrue()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.FlagZero = true;
        bus.WriteWord(0x1000, Asm.Bcc(Condition.Equal, displacement: 4));

        int cycles = cpu.Step();

        Assert.Equal(0x1006u, cpu.PC); // base (0x1002, after the opcode word) + 4
        Assert.Equal(10, cycles);
    }

    [Fact]
    public void Bcc_FallsThroughWhenConditionIsFalse()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.FlagZero = false;
        bus.WriteWord(0x1000, Asm.Bcc(Condition.Equal, displacement: 4));

        int cycles = cpu.Step();

        Assert.Equal(0x1002u, cpu.PC);
        Assert.Equal(8, cycles);
    }

    [Fact]
    public void Bsr_Rts_RoundTripsThroughTheStack()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[7] = 0x2000;
        bus.WriteWord(0x1000, Asm.Bsr(displacement: 4)); // target 0x1002 + 4 = 0x1006
        bus.WriteWord(0x1006, Asm.Rts);

        cpu.Step(); // BSR
        Assert.Equal(0x1006u, cpu.PC);
        Assert.Equal(0x1FFCu, cpu.A[7]);

        cpu.Step(); // RTS
        Assert.Equal(0x1002u, cpu.PC);
        Assert.Equal(0x2000u, cpu.A[7]);
    }

    [Fact]
    public void Jsr_Rts_RoundTripsThroughAbsoluteLongAddressing()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[7] = 0x2000;
        bus.WriteWord(0x1000, Asm.Jsr(eaMode: 7, eaReg: 1)); // absolute long
        bus.WriteLong(0x1002, 0x0000_1010);
        bus.WriteWord(0x1010, Asm.Rts);

        cpu.Step(); // JSR
        Assert.Equal(0x1010u, cpu.PC);
        Assert.Equal(0x1006u, bus.ReadLong(cpu.A[7])); // return address pushed on the stack

        cpu.Step(); // RTS
        Assert.Equal(0x1006u, cpu.PC);
        Assert.Equal(0x2000u, cpu.A[7]);
    }

    [Fact]
    public void AddQuick_ToAddressRegister_OperatesOnFull32BitsAndLeavesFlagsAlone()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x1000;
        cpu.FlagZero = true; // sentinel — ADDQ to An must not touch CCR
        bus.WriteWord(0x1000, Asm.AddQuick(data: 4, Size.Word, eaMode: 1, eaReg: 0));

        cpu.Step();

        Assert.Equal(0x1004u, cpu.A[0]);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Clr_ZeroesDestinationAndSetsZeroFlag()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xFFFF_FFFF;
        bus.WriteWord(0x1000, Asm.Clr(Size.Long, eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0u, cpu.D[0]);
        Assert.True(cpu.FlagZero);
    }

    [Fact]
    public void Nop_TakesFourCyclesAndOnlyAdvancesPc()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        bus.WriteWord(0x1000, Asm.Nop);

        int cycles = cpu.Step();

        Assert.Equal(4, cycles);
        Assert.Equal(0x1002u, cpu.PC);
    }

}
