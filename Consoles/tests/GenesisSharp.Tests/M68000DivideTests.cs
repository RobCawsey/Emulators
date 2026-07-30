using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000DivideTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Divu_DividesUnsigned_PacksQuotientInLowWordAndRemainderInHighWord()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 100;
        cpu.D[1] = 7;
        bus.WriteWord(0x1000, Asm.Divu(dn: 0, eaMode: 0, eaReg: 1)); // D0 = D0 / D1 (unsigned)

        int cycles = cpu.Step();

        Assert.Equal(0x0002_000Eu, cpu.D[0]); // quotient 14, remainder 2
        Assert.False(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagOverflow);
        Assert.False(cpu.FlagCarry);
        Assert.Equal(140, cycles);
    }

    [Fact]
    public void Divs_SignedDivision_TruncatesTowardZeroWithSignMatchingDividendOnRemainder()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = unchecked((uint)-100);
        cpu.D[1] = 7;
        bus.WriteWord(0x1000, Asm.Divs(dn: 0, eaMode: 0, eaReg: 1)); // D0 = D0 / D1 (signed)

        int cycles = cpu.Step();

        Assert.Equal(0xFFFE_FFF2u, cpu.D[0]); // quotient -14, remainder -2
        Assert.True(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.Equal(158, cycles);
    }

    [Fact]
    public void DivideByZero_TrapsViaVectorFive_WithoutModifyingDn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.A[7] = 0x2000;
        cpu.D[0] = 12345;
        cpu.D[1] = 0;
        bus.WriteLong(0x14, 0x9000); // vector 5: divide by zero
        bus.WriteWord(0x1000, Asm.Divu(dn: 0, eaMode: 0, eaReg: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(12345u, cpu.D[0]); // untouched — the (nonexistent) division never happened
        Assert.False(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagOverflow);
        Assert.Equal(38, cycles);
    }

    /// <summary>Overflow leaves Dn unmodified, but N/Z are NOT simply "left alone" despite the
    /// Motorola manual calling them undefined in this case — real hardware consistently forces
    /// N=1/Z=0 (confirmed against the SingleStepTests hardware-test-vector suite via
    /// clown68000's implementation). Flags are deliberately pre-set to the opposite of the
    /// expected outcome here so this test would fail if the fix regressed to "leave untouched."</summary>
    [Fact]
    public void Divu_QuotientOverflowsSixteenBits_ForcesNegativeSetAndZeroClear()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x0002_0000; // 131072
        cpu.D[1] = 2;           // quotient would be 65536 -- doesn't fit in 16 bits unsigned
        cpu.FlagNegative = false;
        cpu.FlagZero = true;
        bus.WriteWord(0x1000, Asm.Divu(dn: 0, eaMode: 0, eaReg: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0002_0000u, cpu.D[0]); // unchanged
        Assert.True(cpu.FlagOverflow);
        Assert.True(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagCarry);
        Assert.Equal(140, cycles);
    }

    [Fact]
    public void Divs_QuotientOverflowsSixteenBits_ForcesNegativeSetAndZeroClear()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x0001_0000; // 65536
        cpu.D[1] = 1;           // quotient would be 65536 -- doesn't fit in a signed 16-bit value
        cpu.FlagNegative = false;
        cpu.FlagZero = true;
        bus.WriteWord(0x1000, Asm.Divs(dn: 0, eaMode: 0, eaReg: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0001_0000u, cpu.D[0]); // unchanged
        Assert.True(cpu.FlagOverflow);
        Assert.True(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.Equal(158, cycles);
    }

    [Fact]
    public void Divu_ZeroQuotient_SetsZeroFlagAndClearsNegative()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 3;
        cpu.D[1] = 7;
        bus.WriteWord(0x1000, Asm.Divu(dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(0x0003_0000u, cpu.D[0]); // quotient 0, remainder 3
        Assert.True(cpu.FlagZero);
        Assert.False(cpu.FlagNegative);
    }
}
