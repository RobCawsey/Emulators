using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000ShiftRotateAndSccDbccTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Asl_Byte_SetsOverflowWhenSignBitChanges()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x40;
        bus.WriteWord(0x1000, Asm.ShiftRegister(ShiftType.Arithmetic, left: true, Size.Byte, count: 1, reg: 0));

        int cycles = cpu.Step();

        Assert.Equal(0x80u, cpu.D[0]);
        Assert.True(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.True(cpu.FlagOverflow);
        Assert.False(cpu.FlagCarry);
        Assert.Equal(8, cycles); // 6 base + 2*1
    }

    [Fact]
    public void Lsr_Word_ShiftsInZeroAndSetsCarryFromLsb()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x0001;
        bus.WriteWord(0x1000, Asm.ShiftRegister(ShiftType.Logical, left: false, Size.Word, count: 1, reg: 0));

        cpu.Step();

        Assert.Equal(0u, cpu.D[0]);
        Assert.True(cpu.FlagZero);
        Assert.True(cpu.FlagCarry);
        Assert.False(cpu.FlagOverflow);
    }

    [Fact]
    public void Rol_Byte_WrapsMsbIntoLsb()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x80;
        bus.WriteWord(0x1000, Asm.ShiftRegister(ShiftType.Rotate, left: true, Size.Byte, count: 1, reg: 0));

        cpu.Step();

        Assert.Equal(0x01u, cpu.D[0]);
        Assert.True(cpu.FlagCarry);
        Assert.False(cpu.FlagNegative);
    }

    [Fact]
    public void Roxl_Byte_RotatesThroughExtendAcrossTwoInstructions()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x80;
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.ShiftRegister(ShiftType.RotateExtend, left: true, Size.Byte, count: 1, reg: 0));
        bus.WriteWord(0x1002, Asm.ShiftRegister(ShiftType.RotateExtend, left: true, Size.Byte, count: 1, reg: 0));

        cpu.Step(); // bit7 (1) shifts out to C/X, X-in was 0 so bit0 becomes 0

        Assert.Equal(0x00u, cpu.D[0]);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagExtend);

        cpu.Step(); // this time X-in is 1, so it rotates into bit0

        Assert.Equal(0x01u, cpu.D[0]);
        Assert.False(cpu.FlagCarry);
        Assert.False(cpu.FlagExtend);
    }

    [Fact]
    public void ShiftRegister_CountZeroFromRegister_LeavesValueAndClearsCarryButNotExtend()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x1234;
        cpu.D[1] = 0; // count register — shifting by zero
        cpu.FlagExtend = true;
        cpu.FlagCarry = true;
        bus.WriteWord(0x1000, Asm.ShiftRegisterByRegisterCount(ShiftType.Logical, left: true, Size.Word, countReg: 1, reg: 0));

        cpu.Step();

        Assert.Equal(0x1234u, cpu.D[0]);
        Assert.False(cpu.FlagCarry);
        Assert.False(cpu.FlagOverflow);
        Assert.True(cpu.FlagExtend); // untouched when count == 0
    }

    [Fact]
    public void ShiftMemory_AlwaysShiftsByExactlyOneBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteWord(0x3000, 0x8001);
        bus.WriteWord(0x1000, Asm.ShiftMemory(ShiftType.Logical, left: true, eaMode: 2, eaReg: 0)); // LSL.W (A0)

        cpu.Step();

        Assert.Equal(0x0002, bus.ReadWord(0x3000));
        Assert.True(cpu.FlagCarry); // bit15 (1) shifted out
    }

    [Fact]
    public void Scc_SetsLowByteOnlyAndLeavesUpperBitsAlone()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[5] = 0x0000_1234;
        cpu.FlagZero = true;
        bus.WriteWord(0x1000, Asm.Scc(Condition.Equal, eaMode: 0, eaReg: 5));

        cpu.Step();

        Assert.Equal(0x0000_12FFu, cpu.D[5]);
    }

    [Fact]
    public void Scc_FalseCondition_ClearsLowByte()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[5] = 0x0000_1234;
        cpu.FlagZero = false;
        bus.WriteWord(0x1000, Asm.Scc(Condition.Equal, eaMode: 0, eaReg: 5));

        cpu.Step();

        Assert.Equal(0x0000_1200u, cpu.D[5]);
    }

    [Fact]
    public void Dbcc_BranchesWhileConditionFalseAndCounterHasNotExpired()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 1;
        bus.WriteWord(0x1000, Asm.Dbcc(Condition.False, reg: 0));
        bus.WriteWord(0x1002, 0xFFFE); // displacement -2: base is 0x1002 (right after the opcode word), so target = 0x1000

        int cycles = cpu.Step();

        Assert.Equal(0u, cpu.D[0] & 0xFFFF);
        Assert.Equal(0x1000u, cpu.PC);
        Assert.Equal(10, cycles);
    }

    [Fact]
    public void Dbcc_CounterExpires_FallsThroughWithoutBranching()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0;
        bus.WriteWord(0x1000, Asm.Dbcc(Condition.False, reg: 0));
        bus.WriteWord(0x1002, 0xFFFC);

        int cycles = cpu.Step();

        Assert.Equal(0xFFFFu, cpu.D[0] & 0xFFFF);
        Assert.Equal(0x1004u, cpu.PC); // falls through, no branch
        Assert.Equal(14, cycles);
    }

    [Fact]
    public void Dbcc_ConditionAlreadyTrue_EndsLoopWithoutTouchingCounter()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 5;
        bus.WriteWord(0x1000, Asm.Dbcc(Condition.True, reg: 0));
        bus.WriteWord(0x1002, 0xFFFC);

        int cycles = cpu.Step();

        Assert.Equal(5u, cpu.D[0]);
        Assert.Equal(0x1004u, cpu.PC);
        Assert.Equal(12, cycles);
    }
}
