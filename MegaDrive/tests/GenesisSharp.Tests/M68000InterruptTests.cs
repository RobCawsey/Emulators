using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000InterruptTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void RaiseInterrupt_AboveCurrentMask_IsServicedOnNextStep()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000; // supervisor, mask = 0
        cpu.A[7] = 0x3000;
        bus.WriteLong(0x70, 0x9000); // vector 28 = 24 + level 4
        bus.WriteWord(0x1000, 0x4E71); // NOP, in case the interrupt is (wrongly) not taken

        cpu.RaiseInterrupt(4);
        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.Equal(0, cpu.PendingInterruptLevel);
        Assert.True(cpu.Supervisor);
        Assert.Equal(4, (cpu.SR >> 8) & 7); // mask raised to the serviced level
        Assert.Equal(0x2FFAu, cpu.A[7]); // pushed a long PC + word SR = 6 bytes
        Assert.Equal(0x1000u, bus.ReadLong(0x2FFC)); // return address
        Assert.Equal(44, cycles);
    }

    [Fact]
    public void RaiseInterrupt_AtOrBelowCurrentMask_IsNotServiced()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2400; // mask = 4
        bus.WriteWord(0x1000, 0x4E71); // NOP

        cpu.RaiseInterrupt(3);
        int cycles = cpu.Step();

        Assert.Equal(0x1002u, cpu.PC); // the NOP ran instead
        Assert.Equal(3, cpu.PendingInterruptLevel); // still pending, not dropped
        Assert.Equal(4, cycles);
    }

    [Fact]
    public void RaiseInterrupt_Level7_IsAlwaysServiced_EvenAtFullMask()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2700; // mask = 7, fully masked
        cpu.A[7] = 0x3000;
        bus.WriteLong((24 + 7) * 4, 0xA000);

        cpu.RaiseInterrupt(7);
        cpu.Step();

        Assert.Equal(0xA000u, cpu.PC);
    }

    [Fact]
    public void RaiseInterrupt_HigherLevelAlreadyPending_IsNotDowngraded()
    {
        var (cpu, _) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2700; // fully masked so nothing gets serviced mid-test

        cpu.RaiseInterrupt(2);
        cpu.RaiseInterrupt(5);
        cpu.RaiseInterrupt(3); // lower than what's already pending — must not overwrite

        Assert.Equal(5, cpu.PendingInterruptLevel);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void RaiseInterrupt_OutOfRange_Throws(int level)
    {
        var (cpu, _) = CreateCpu();

        Assert.Throws<ArgumentOutOfRangeException>(() => cpu.RaiseInterrupt(level));
    }

    [Fact]
    public void RaiseInterrupt_WakesTheCpuFromStop()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.A[7] = 0x3000;
        bus.WriteWord(0x1000, 0x4E72); // STOP
        bus.WriteWord(0x1002, 0x2100); // new SR: supervisor, mask = 1
        bus.WriteLong(0x70, 0x9000); // vector for level 4

        cpu.Step(); // executes STOP
        Assert.True(cpu.Stopped);

        cpu.RaiseInterrupt(4);
        int cycles = cpu.Step();

        Assert.False(cpu.Stopped);
        Assert.Equal(0x9000u, cpu.PC);
        Assert.Equal(44, cycles);
    }

    [Fact]
    public void Stopped_WithNoQualifyingInterrupt_KeepsIdling()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        bus.WriteWord(0x1000, 0x4E72); // STOP
        bus.WriteWord(0x1002, 0x2500); // new SR: mask = 5

        cpu.Step(); // executes STOP
        cpu.RaiseInterrupt(3); // below the mask — should not wake it

        int cycles = cpu.Step();

        Assert.True(cpu.Stopped);
        Assert.Equal(4, cycles);
    }
}
