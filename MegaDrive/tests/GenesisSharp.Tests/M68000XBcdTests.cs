using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000XBcdTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Addx_RegisterForm_AddsWithNoCarryIn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 5; // dst (Dx)
        cpu.D[0] = 3; // src (Dy)
        cpu.FlagExtend = false;
        cpu.FlagZero = true; // sentinel — must be cleared since the result is nonzero
        bus.WriteWord(0x1000, Asm.Addx(Size.Byte, dx: 1, predecrement: false, ry: 0)); // ADDX D0,D1

        int cycles = cpu.Step();

        Assert.Equal(8u, cpu.D[1]);
        Assert.False(cpu.FlagZero);
        Assert.False(cpu.FlagCarry);
        Assert.Equal(4, cycles);
    }

    [Fact]
    public void Addx_ZeroResult_LeavesZeroFlagUnchangedRatherThanForcingIt()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0xFF; // dst
        cpu.D[0] = 0x00; // src
        cpu.FlagExtend = true; // carry-in of 1: 0xFF + 0x00 + 1 wraps to 0x00
        cpu.FlagZero = false; // starts false — must still be false afterwards, not forced true
        bus.WriteWord(0x1000, Asm.Addx(Size.Byte, dx: 1, predecrement: false, ry: 0));

        cpu.Step();

        Assert.Equal(0u, cpu.D[1]);
        Assert.False(cpu.FlagZero);
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagExtend);
    }

    [Fact]
    public void Addx_ZeroResult_LeavesPreviouslySetZeroFlagAlone()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0xFF;
        cpu.D[0] = 0x00;
        cpu.FlagExtend = true;
        cpu.FlagZero = true; // starts true — a real multi-limb chain relies on this surviving

        bus.WriteWord(0x1000, Asm.Addx(Size.Byte, dx: 1, predecrement: false, ry: 0));

        cpu.Step();

        Assert.Equal(0u, cpu.D[1]);
        Assert.True(cpu.FlagZero); // untouched, because this limb's result was zero
    }

    [Fact]
    public void Addx_Predecrement_ReadsBothOperandsFromMemoryAndUpdatesBothPointers()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3002; // src pointer (Ay)
        cpu.A[1] = 0x3006; // dst pointer (Ax)
        bus.WriteWord(0x3000, 5); // src value, at Ay-2
        bus.WriteWord(0x3004, 3); // dst value, at Ax-2
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.Addx(Size.Word, dx: 1, predecrement: true, ry: 0)); // ADDX -(A0),-(A1)

        int cycles = cpu.Step();

        Assert.Equal(8, bus.ReadWord(0x3004));
        Assert.Equal(0x3000u, cpu.A[0]);
        Assert.Equal(0x3004u, cpu.A[1]);
        Assert.Equal(18, cycles);
    }

    [Fact]
    public void SubaLong_AnDirectSource_IsNotMisroutedToSubx()
    {
        // Found via real-ROM testing: SUBA.L A0,A1 -- ordinary pointer-subtraction code
        // (exactly what GCC/SGDK emit right after a strlen-style null-terminator scan) --
        // shares SUBX's bit shape whenever its <ea> source is Dn/An direct, and was being
        // misrouted into ExecuteSubx, which rejected it as a reserved opcode.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x0076D3C;
        cpu.A[1] = 0x0076D54;
        bus.WriteWord(0x1000, Asm.SubaLong(an: 1, eaMode: 1, eaReg: 0)); // SUBA.L A0,A1

        cpu.Step();

        Assert.Equal(0x18u, cpu.A[1]); // 0x76D54 - 0x76D3C
    }

    [Fact]
    public void AddaLong_AnDirectSource_IsNotMisroutedToAddx()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x100;
        cpu.A[1] = 0x200;
        bus.WriteWord(0x1000, Asm.AddaLong(an: 1, eaMode: 1, eaReg: 0)); // ADDA.L A0,A1

        cpu.Step();

        Assert.Equal(0x300u, cpu.A[1]);
    }

    [Fact]
    public void Subx_RegisterForm_SubtractsWithNoBorrow()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 10; // dst
        cpu.D[0] = 3;  // src
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.Subx(Size.Byte, dx: 1, predecrement: false, ry: 0)); // SUBX D0,D1

        cpu.Step();

        Assert.Equal(7u, cpu.D[1]);
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void Subx_Borrow_WrapsAndSetsCarryAndExtend()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 3; // dst
        cpu.D[0] = 5; // src
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.Subx(Size.Byte, dx: 1, predecrement: false, ry: 0));

        cpu.Step();

        Assert.Equal(0xFEu, cpu.D[1]); // 3 - 5 = -2, wraps to 0xFE
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagExtend);
    }

    [Fact]
    public void Abcd_RegisterForm_AddsPackedDecimalDigits()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0x09; // dst (Dx)
        cpu.D[0] = 0x01; // src (Dy)
        cpu.FlagExtend = false;
        cpu.FlagZero = true; // sentinel, must be cleared
        bus.WriteWord(0x1000, Asm.Abcd(dx: 1, predecrement: false, ry: 0)); // ABCD D0,D1

        int cycles = cpu.Step();

        Assert.Equal(0x10u, cpu.D[1]); // 09 + 01 = 10 in BCD
        Assert.False(cpu.FlagCarry);
        Assert.False(cpu.FlagZero);
        Assert.Equal(6, cycles);
    }

    [Fact]
    public void Abcd_OverflowsPastNinetyNine_CarriesOutAndWrapsToZero()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0x99; // dst
        cpu.D[0] = 0x01; // src
        cpu.FlagExtend = false;
        cpu.FlagZero = false;
        bus.WriteWord(0x1000, Asm.Abcd(dx: 1, predecrement: false, ry: 0));

        cpu.Step();

        Assert.Equal(0x00u, cpu.D[1]); // 99 + 01 = 100, wraps to 00
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagExtend);
        Assert.False(cpu.FlagZero); // left unchanged (was false), not forced true by the zero result
    }

    [Fact]
    public void Sbcd_RegisterForm_SubtractsPackedDecimalDigits()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0x10; // dst
        cpu.D[0] = 0x01; // src
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.Sbcd(dx: 1, predecrement: false, ry: 0)); // SBCD D0,D1

        cpu.Step();

        Assert.Equal(0x09u, cpu.D[1]); // 10 - 01 = 09 in BCD
        Assert.False(cpu.FlagCarry);
    }

    [Fact]
    public void Sbcd_Borrow_WrapsToNinetyNineWithCarry()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0x00; // dst
        cpu.D[0] = 0x01; // src
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.Sbcd(dx: 1, predecrement: false, ry: 0));

        cpu.Step();

        Assert.Equal(0x99u, cpu.D[1]); // 00 - 01 borrows to 99
        Assert.True(cpu.FlagCarry);
        Assert.True(cpu.FlagExtend);
    }

    [Fact]
    public void Sbcd_Predecrement_ReadsBothOperandsFromMemoryAndUpdatesBothPointers()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3001; // src pointer (Ay)
        cpu.A[1] = 0x3002; // dst pointer (Ax)
        bus.WriteByte(0x3000, 0x01); // src value, at Ay-1
        bus.WriteByte(0x3001, 0x10); // dst value, at Ax-1
        cpu.FlagExtend = false;
        bus.WriteWord(0x1000, Asm.Sbcd(dx: 1, predecrement: true, ry: 0)); // SBCD -(A0),-(A1)

        int cycles = cpu.Step();

        Assert.Equal(0x09, bus.ReadByte(0x3001));
        Assert.Equal(0x3000u, cpu.A[0]);
        Assert.Equal(0x3001u, cpu.A[1]);
        Assert.Equal(18, cycles);
    }

    [Fact]
    public void Or_RegisterForm_OpmodeFiveStillDecodesCorrectly_NotMistakenForSbcd()
    {
        // Regression check: SBCD only occupies opmode 4 (bits 8-6 = 100); OR.W at opmode 5
        // with an EA-mode-000 destination must still reach ExecuteOr, not ExecuteSbcd.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[1] = 0x0F0F;
        cpu.D[2] = 0xFF00;
        bus.WriteWord(0x1000, Asm.OrEaOrDn(Size.Word, dn: 1, eaMode: 0, eaReg: 2));

        cpu.Step();

        Assert.Equal(0xFF0Fu, cpu.D[2]);
    }
}
