using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

public class Z80InterruptTests
{
    private static (Z80 Cpu, FlatZ80Bus Bus) CreateCpu()
    {
        var bus = new FlatZ80Bus();
        return (new Z80(bus), bus);
    }

    [Fact]
    public void Nmi_IsServicedRegardlessOfIff1_AndSavesTheOldIff1IntoIff2()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SP = 0x2000;
        cpu.Iff1 = false; // interrupts disabled — NMI must fire anyway
        cpu.Iff2 = true;

        cpu.RaiseNonMaskableInterrupt();
        int cycles = cpu.Step();

        Assert.Equal(0x0066u, cpu.PC);
        Assert.False(cpu.Iff1);
        Assert.False(cpu.Iff2); // saved the pre-NMI IFF1 value (false), not left alone
        Assert.Equal(0x1000, bus.ReadByte(0x1FFE) | (bus.ReadByte(0x1FFF) << 8));
        Assert.Equal(11, cycles);
    }

    [Fact]
    public void Nmi_ThenRetn_RestoresTheOriginalIff1()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SP = 0x2000;
        cpu.Iff1 = true; // interrupts were enabled before the NMI

        cpu.RaiseNonMaskableInterrupt();
        cpu.Step(); // NMI: IFF1 -> false, IFF2 <- true (saved)

        bus.WriteByte(0x0066, 0xED);
        bus.WriteByte(0x0067, 0x45); // RETN
        cpu.Step();

        Assert.True(cpu.Iff1); // restored from IFF2
    }

    [Fact]
    public void MaskableInterrupt_Im1_IsIgnoredWhileIff1IsClear()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.Iff1 = false;
        cpu.InterruptMode = 1;
        bus.WriteByte(0x1000, 0x00); // NOP

        cpu.RaiseMaskableInterrupt();
        int cycles = cpu.Step();

        Assert.Equal(0x1001u, cpu.PC); // the NOP ran instead
        Assert.True(cpu.InterruptPending); // still pending
        Assert.Equal(4, cycles);
    }

    [Fact]
    public void MaskableInterrupt_Im1_JumpsToFixedVectorWhenEnabled()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SP = 0x2000;
        cpu.Iff1 = true;
        cpu.InterruptMode = 1;

        cpu.RaiseMaskableInterrupt();
        int cycles = cpu.Step();

        Assert.Equal(0x0038u, cpu.PC);
        Assert.False(cpu.Iff1);
        Assert.False(cpu.InterruptPending);
        Assert.Equal(13, cycles);
    }

    [Fact]
    public void MaskableInterrupt_Im2_ReadsVectorFromTable()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SP = 0x2000;
        cpu.Iff1 = true;
        cpu.InterruptMode = 2;
        cpu.I = 0x40;
        bus.WriteWord(0x4010, 0x8000); // vector table entry at (I<<8)|dataBusValue

        cpu.RaiseMaskableInterrupt(0x10);
        int cycles = cpu.Step();

        Assert.Equal(0x8000u, cpu.PC);
        Assert.Equal(19, cycles);
    }

    [Fact]
    public void MaskableInterrupt_WakesTheCpuFromHalt()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SP = 0x2000;
        cpu.Iff1 = true;
        cpu.InterruptMode = 1;
        bus.WriteByte(0x1000, 0x76); // HALT

        cpu.Step();
        Assert.True(cpu.Halted);

        cpu.RaiseMaskableInterrupt();
        cpu.Step();

        Assert.False(cpu.Halted);
        Assert.Equal(0x0038u, cpu.PC);
    }

    [Fact]
    public void Nmi_WakesTheCpuFromHalt_EvenWithInterruptsDisabled()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SP = 0x2000;
        cpu.Iff1 = false;
        bus.WriteByte(0x1000, 0x76); // HALT

        cpu.Step();
        Assert.True(cpu.Halted);

        cpu.RaiseNonMaskableInterrupt();
        cpu.Step();

        Assert.False(cpu.Halted);
        Assert.Equal(0x0066u, cpu.PC);
    }
}
