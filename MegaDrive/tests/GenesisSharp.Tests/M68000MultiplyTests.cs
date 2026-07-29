using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000MultiplyTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Mulu_MultipliesUnsignedWordsIntoFullLongResult()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xFFFF; // treated unsigned: 65535
        cpu.D[1] = 3;
        bus.WriteWord(0x1000, Asm.Mulu(dn: 0, eaMode: 0, eaReg: 1)); // D0 = D0 * D1 (unsigned)

        int cycles = cpu.Step();

        Assert.Equal(196605u, cpu.D[0]); // 65535 * 3
        Assert.False(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagOverflow);
        Assert.False(cpu.FlagCarry);
        Assert.Equal(38 + 2 * 2, cycles); // source D1=3=0b11 has two one-bits
    }

    [Fact]
    public void Muls_TreatsBothOperandsAsSignedAndProducesNegativeResult()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = unchecked((uint)(short)-4);
        cpu.D[1] = 5;
        bus.WriteWord(0x1000, Asm.Muls(dn: 0, eaMode: 0, eaReg: 1)); // D0 = D0 * D1 (signed)

        cpu.Step();

        Assert.Equal(unchecked((uint)-20), cpu.D[0]);
        Assert.True(cpu.FlagNegative);
        Assert.False(cpu.FlagZero);
    }

    [Fact]
    public void Mulu_ZeroResult_SetsZeroFlagAndClearsNegative()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0;
        cpu.D[1] = 1234;
        bus.WriteWord(0x1000, Asm.Mulu(dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(0u, cpu.D[0]);
        Assert.True(cpu.FlagZero);
        Assert.False(cpu.FlagNegative);
    }

    [Fact]
    public void Mulu_OnlyReadsTheLowWordOfDn_LeavingTheHighWordOfSourceUntouched()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xABCD_0002; // low word = 2, high word must be ignored as the multiplicand
        cpu.D[1] = 3;
        bus.WriteWord(0x1000, Asm.Mulu(dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(6u, cpu.D[0]);
    }

    [Fact]
    public void Muls_CyclesCount_UsesZeroBitPopcountForNegativeSource()
    {
        // Per the Motorola manual, MULS's data-dependent cycle term counts one-bits for a
        // non-negative source but zero-bits for a negative one. Source word 0xFFFE (-2) has
        // exactly one zero-bit (bit 0), so n=1 regardless of the destination's sign.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 7;
        cpu.D[1] = unchecked((uint)(short)-2);
        bus.WriteWord(0x1000, Asm.Muls(dn: 0, eaMode: 0, eaReg: 1));

        int cycles = cpu.Step();

        Assert.Equal(38 + 2 * 1, cycles);
    }
}
