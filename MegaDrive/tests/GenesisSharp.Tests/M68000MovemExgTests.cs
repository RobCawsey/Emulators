using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000MovemExgTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Movem_StoreToPredecrement_WritesInDescendingPriorityOrderAndUpdatesAn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[7] = 0x2000;
        cpu.D[0] = 0x1111_1111; // D0
        cpu.D[1] = 0x2222_2222; // D1
        cpu.A[0] = 0x3333_3333; // A0
        bus.WriteWord(0x1000, Asm.Movem(load: false, Size.Long, eaMode: 4, eaReg: 7)); // MOVEM.L D0/D1/A0,-(A7)
        bus.WriteWord(0x1002, Asm.MaskForStore(0, 1, 8));

        int cycles = cpu.Step();

        // Priority order for -(An) is A7..A0, D7..D0, so among {D0,D1,A0} the write order is A0, D1, D0.
        Assert.Equal(0x3333_3333u, bus.ReadLong(0x1FFC)); // A0 written first, at the highest address
        Assert.Equal(0x2222_2222u, bus.ReadLong(0x1FF8));
        Assert.Equal(0x1111_1111u, bus.ReadLong(0x1FF4));
        Assert.Equal(0x1FF4u, cpu.A[7]);
        Assert.Equal(28, cycles); // 4 + 3 registers * 8 (long)
    }

    [Fact]
    public void Movem_LoadFromPostincrement_ReadsInAscendingOrderAndUpdatesAn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteLong(0x3000, 0xAAAA_AAAA); // -> D0
        bus.WriteLong(0x3004, 0xBBBB_BBBB); // -> D1
        bus.WriteLong(0x3008, 0xCCCC_CCCC); // -> A2
        bus.WriteWord(0x1000, Asm.Movem(load: true, Size.Long, eaMode: 3, eaReg: 0)); // MOVEM.L (A0)+,D0/D1/A2
        bus.WriteWord(0x1002, Asm.MaskForLoad(0, 1, 10));

        cpu.Step();

        Assert.Equal(0xAAAA_AAAAu, cpu.D[0]);
        Assert.Equal(0xBBBB_BBBBu, cpu.D[1]);
        Assert.Equal(0xCCCC_CCCCu, cpu.A[2]);
        Assert.Equal(0x300Cu, cpu.A[0]);
    }

    [Fact]
    public void Movem_WordLoad_SignExtendsIntoFullRegisterAndLeavesPointerAlone()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[0] = 0x3000;
        bus.WriteWord(0x3000, 0xFFFE); // -2 as a signed word
        bus.WriteWord(0x1000, Asm.Movem(load: true, Size.Word, eaMode: 2, eaReg: 0)); // MOVEM.W (A0),D0
        bus.WriteWord(0x1002, Asm.MaskForLoad(0));

        cpu.Step();

        Assert.Equal(0xFFFF_FFFEu, cpu.D[0]);
        Assert.Equal(0x3000u, cpu.A[0]); // (An) mode doesn't move the pointer
    }

    [Fact]
    public void Exg_DataToData_SwapsBothRegisters()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[2] = 0xAAAA;
        cpu.D[3] = 0xBBBB;
        bus.WriteWord(0x1000, Asm.ExgDataData(2, 3));

        int cycles = cpu.Step();

        Assert.Equal(0xBBBBu, cpu.D[2]);
        Assert.Equal(0xAAAAu, cpu.D[3]);
        Assert.Equal(6, cycles);
    }

    [Fact]
    public void Exg_AddressToAddress_SwapsBothRegisters()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[1] = 0x1000_0000;
        cpu.A[2] = 0x2000_0000;
        bus.WriteWord(0x1000, Asm.ExgAddressAddress(1, 2));

        cpu.Step();

        Assert.Equal(0x2000_0000u, cpu.A[1]);
        Assert.Equal(0x1000_0000u, cpu.A[2]);
    }

    [Fact]
    public void Exg_DataToAddress_SwapsAcrossRegisterFiles()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x1111_1111;
        cpu.A[1] = 0x2222_2222;
        bus.WriteWord(0x1000, Asm.ExgDataAddress(0, 1));

        cpu.Step();

        Assert.Equal(0x2222_2222u, cpu.D[0]);
        Assert.Equal(0x1111_1111u, cpu.A[1]);
    }

    [Fact]
    public void And_RegisterForm_StillDecodesCorrectly_NotMistakenForExg()
    {
        // Regression check: EXG's dispatch check must not swallow ordinary AND opcodes that
        // happen to share the same top nibble.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0xF0F0;
        cpu.D[1] = 0x0FF0;
        bus.WriteWord(0x1000, Asm.AndDnAndEa(Size.Word, dn: 0, eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(0x00F0u, cpu.D[0]);
    }
}
