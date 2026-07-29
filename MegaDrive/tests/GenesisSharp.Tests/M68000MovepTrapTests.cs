using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000MovepTrapTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Movep_RegisterToMemory_Word_WritesHighByteFirstAtAlternatingAddresses()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x1234;
        cpu.A[0] = 0x3000;
        bus.WriteWord(0x1000, Asm.Movep(dataRegister: 0, registerToMemory: true, isLong: false, addressRegister: 0));
        bus.WriteWord(0x1002, 0); // displacement

        int cycles = cpu.Step();

        Assert.Equal(0x12, bus.ReadByte(0x3000));
        Assert.Equal(0x34, bus.ReadByte(0x3002));
        Assert.Equal(0x3000u, cpu.A[0]); // MOVEP never touches the address register
        Assert.Equal(16, cycles);
    }

    [Fact]
    public void Movep_RegisterToMemory_Long_WritesFourBytesHighestFirst()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x1234_5678;
        cpu.A[0] = 0x3000;
        bus.WriteWord(0x1000, Asm.Movep(dataRegister: 0, registerToMemory: true, isLong: true, addressRegister: 0));
        bus.WriteWord(0x1002, 0);

        int cycles = cpu.Step();

        Assert.Equal(0x12, bus.ReadByte(0x3000));
        Assert.Equal(0x34, bus.ReadByte(0x3002));
        Assert.Equal(0x56, bus.ReadByte(0x3004));
        Assert.Equal(0x78, bus.ReadByte(0x3006));
        Assert.Equal(24, cycles);
    }

    [Fact]
    public void Movep_MemoryToRegister_Word_OnlyReplacesLowWord()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xFFFF_FFFF;
        cpu.A[0] = 0x3000;
        bus.WriteByte(0x3000, 0xAB);
        bus.WriteByte(0x3002, 0xCD);
        bus.WriteWord(0x1000, Asm.Movep(dataRegister: 0, registerToMemory: false, isLong: false, addressRegister: 0));
        bus.WriteWord(0x1002, 0);

        cpu.Step();

        Assert.Equal(0xFFFF_ABCDu, cpu.D[0]);
    }

    [Fact]
    public void Movep_MemoryToRegister_Long_ReplacesWholeRegister()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteByte(0x3000, 0x11);
        bus.WriteByte(0x3002, 0x22);
        bus.WriteByte(0x3004, 0x33);
        bus.WriteByte(0x3006, 0x44);
        bus.WriteWord(0x1000, Asm.Movep(dataRegister: 0, registerToMemory: false, isLong: true, addressRegister: 0));
        bus.WriteWord(0x1002, 0);

        cpu.Step();

        Assert.Equal(0x1122_3344u, cpu.D[0]);
    }

    [Fact]
    public void Movep_AppliesSignedDisplacementFromAddressRegister()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xAABB;
        cpu.A[0] = 0x2FF0;
        bus.WriteWord(0x1000, Asm.Movep(dataRegister: 0, registerToMemory: true, isLong: false, addressRegister: 0));
        bus.WriteWord(0x1002, 0x0010); // +0x10 -> effective address 0x3000

        cpu.Step();

        Assert.Equal(0xAA, bus.ReadByte(0x3000));
        Assert.Equal(0xBB, bus.ReadByte(0x3002));
    }

    [Fact]
    public void Trap_RaisesExceptionViaVector32PlusNumber()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000;
        cpu.A[7] = 0x2000;
        bus.WriteLong((32 + 5) * 4, 0x9000); // vector for TRAP #5
        bus.WriteWord(0x1000, Asm.Trap(5));

        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(0x1FFAu, cpu.A[7]);
        Assert.Equal(0x1002u, bus.ReadLong(0x1FFC)); // return address
        Assert.Equal(0x0000, bus.ReadWord(0x1FFA)); // saved SR, unmodified by TRAP itself
        Assert.Equal(34, cycles);
    }

    [Fact]
    public void Trap_DifferentVectorNumbers_UseDifferentVectorAddresses()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[7] = 0x2000;
        bus.WriteLong((32 + 0) * 4, 0x8000);
        bus.WriteLong((32 + 15) * 4, 0xA000);
        bus.WriteWord(0x1000, Asm.Trap(0));

        cpu.Step();

        Assert.Equal(0x8000u, cpu.PC);

        cpu.PC = 0x2000;
        cpu.A[7] = 0x2000;
        bus.WriteWord(0x2000, Asm.Trap(15));
        cpu.Step();

        Assert.Equal(0xA000u, cpu.PC);
    }

    [Fact]
    public void BitOp_Dynamic_StillDecodesCorrectly_NotMistakenForMovep()
    {
        // Regression check: MOVEP only claims EA mode 001; every other EA mode in this
        // sub-space is still a dynamic bit op.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0b0000_0100;
        cpu.D[5] = 2; // bit number register
        bus.WriteWord(0x1000, Asm.BitOpDynamic(opType: 0, bitRegister: 5, eaMode: 0, eaReg: 0)); // BTST D5,D0

        cpu.Step();

        Assert.False(cpu.FlagZero);
        Assert.Equal(0b0000_0100u, cpu.D[0]);
    }
}
