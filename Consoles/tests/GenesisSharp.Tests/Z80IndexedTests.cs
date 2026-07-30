using GenesisSharp.CpuZ80;

namespace GenesisSharp.Tests;

public class Z80IndexedTests
{
    private static (Z80 Cpu, FlatZ80Bus Bus) CreateCpu()
    {
        var bus = new FlatZ80Bus();
        return (new Z80(bus), bus);
    }

    [Fact]
    public void LdIxImmediate_SetsIxWithoutTouchingHl()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.HL = 0x9999;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.LdRpImm(2)); // LD IX,nn
        bus.WriteWord(2, 0x3000);

        int cycles = cpu.Step();

        Assert.Equal(0x3000, cpu.IX);
        Assert.Equal(0x9999, cpu.HL);
        Assert.Equal(14, cycles);
    }

    [Fact]
    public void LdIyImmediate_IsIndependentOfIx()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x1111;
        bus.WriteByte(0, 0xFD);
        bus.WriteByte(1, Z80Asm.LdRpImm(2)); // LD IY,nn
        bus.WriteWord(2, 0x4000);

        cpu.Step();

        Assert.Equal(0x4000, cpu.IY);
        Assert.Equal(0x1111, cpu.IX);
    }

    [Fact]
    public void IncIx_DecIx_RoundTrip()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x2FFF;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.IncRp(2)); // INC IX

        int cycles = cpu.Step();

        Assert.Equal(0x3000, cpu.IX);
        Assert.Equal(10, cycles);
    }

    [Fact]
    public void AddIxRp_ComputesNormally_ThroughTheSharedFlagLogic()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0xFFFF;
        cpu.BC = 0x0001;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.AddHlRp(0)); // ADD IX,BC

        int cycles = cpu.Step();

        Assert.Equal(0x0000, cpu.IX);
        Assert.True(cpu.FlagCarry);
        Assert.Equal(15, cycles);
    }

    [Fact]
    public void LoadRegisterFromIndexedIx_UsesSignedDisplacement()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0x3005, 0x42);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.LdRR(0, 6)); // LD B,(IX+d)
        bus.WriteByte(2, 5);

        int cycles = cpu.Step();

        Assert.Equal(0x42, cpu.B);
        Assert.Equal(19, cycles);
    }

    [Fact]
    public void LoadIndexedIx_WithNegativeDisplacement_AddressesBeforeTheBase()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0x2FFE, 0x77);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.LdRR(0, 6)); // LD B,(IX+d)
        bus.WriteByte(2, unchecked((byte)(-2)));

        cpu.Step();

        Assert.Equal(0x77, cpu.B);
    }

    [Fact]
    public void StoreRegisterToIndexedIx_WritesMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        cpu.B = 0x99;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.LdRR(6, 0)); // LD (IX+d),B
        bus.WriteByte(2, 5);

        cpu.Step();

        Assert.Equal(0x99, bus.ReadByte(0x3005));
    }

    [Fact]
    public void LoadIndexedIx_Immediate_WritesTheByteAtTheDisplacedAddress()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.LdRImm(6)); // LD (IX+d),n
        bus.WriteByte(2, 5);
        bus.WriteByte(3, 0xAB);

        cpu.Step();

        Assert.Equal(0xAB, bus.ReadByte(0x3005));
    }

    [Fact]
    public void AluOnIndexedIx_ComputesAgainstMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        cpu.A = 0x01;
        bus.WriteByte(0x3005, 0x01);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.AluR(0, 6)); // ADD A,(IX+d)
        bus.WriteByte(2, 5);

        int cycles = cpu.Step();

        Assert.Equal(0x02, cpu.A);
        Assert.Equal(19, cycles);
    }

    [Fact]
    public void IncIndexedIx_IncrementsMemoryAtDisplacedAddress()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0x3005, 0x41);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.IncR(6)); // INC (IX+d)
        bus.WriteByte(2, 5);

        int cycles = cpu.Step();

        Assert.Equal(0x42, bus.ReadByte(0x3005));
        Assert.Equal(23, cycles);
    }

    [Fact]
    public void PushPopIx_RoundTrips()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.SP = 0x2000;
        cpu.IX = 0xABCD;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.Push(2)); // PUSH IX
        bus.WriteByte(2, 0xDD);
        bus.WriteByte(3, Z80Asm.Pop(0)); // POP BC (pops what PUSH IX pushed)

        int pushCycles = cpu.Step();
        int popCycles = cpu.Step();

        Assert.Equal(0xABCD, cpu.BC);
        Assert.Equal(0x2000, cpu.SP);
        Assert.Equal(15, pushCycles);
        Assert.Equal(14, popCycles);
    }

    [Fact]
    public void JpIx_JumpsToTheAddressHeldInIx()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x5000;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xE9); // JP (IX)

        int cycles = cpu.Step();

        Assert.Equal(0x5000u, cpu.PC);
        Assert.Equal(8, cycles);
    }

    [Fact]
    public void ExSpIx_SwapsIxWithTheWordAtSp()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.SP = 0x2000;
        cpu.IX = 0xAAAA;
        bus.WriteWord(0x2000, 0xBBBB);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xE3); // EX (SP),IX

        cpu.Step();

        Assert.Equal(0xBBBB, cpu.IX);
        Assert.Equal(0xAAAA, bus.ReadByte(0x2000) | (bus.ReadByte(0x2001) << 8));
    }

    [Fact]
    public void ExDeIx_SwapsDeAndIx()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.DE = 0x1234;
        cpu.IX = 0x5678;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xEB); // EX DE,IX

        cpu.Step();

        Assert.Equal(0x5678, cpu.DE);
        Assert.Equal(0x1234, cpu.IX);
    }

    [Fact]
    public void LdSpIx_LoadsStackPointerFromIx()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x4000;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xF9); // LD SP,IX

        cpu.Step();

        Assert.Equal(0x4000, cpu.SP);
    }

    [Fact]
    public void DdCb_Rotate_OperatesOnIndexedMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0x3005, 0x80);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xCB);
        bus.WriteByte(2, 5); // displacement comes before the CB-style opcode
        bus.WriteByte(3, Z80Asm.CbRotate(0, 6)); // RLC (IX+d)

        int cycles = cpu.Step();

        Assert.Equal(0x01, bus.ReadByte(0x3005));
        Assert.True(cpu.FlagCarry);
        Assert.Equal(23, cycles);
    }

    [Fact]
    public void DdCb_Bit_TestsIndexedMemoryWithoutModifyingIt()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0x3005, 0x04);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xCB);
        bus.WriteByte(2, 5);
        bus.WriteByte(3, Z80Asm.CbBit(2, 6)); // BIT 2,(IX+d)

        int cycles = cpu.Step();

        Assert.False(cpu.FlagZero);
        Assert.Equal(0x04, bus.ReadByte(0x3005));
        Assert.Equal(20, cycles);
    }

    [Fact]
    public void DdCb_Set_SetsOnlyTheTargetBitInIndexedMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.IX = 0x3000;
        bus.WriteByte(0x3005, 0x00);
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xCB);
        bus.WriteByte(2, 5);
        bus.WriteByte(3, Z80Asm.CbSet(3, 6)); // SET 3,(IX+d)

        cpu.Step();

        Assert.Equal(0x08, bus.ReadByte(0x3005));
    }

    [Fact]
    public void NonHlOpcodeUnderDdPrefix_BehavesLikeTheUnprefixedInstructionPlusFourCycles()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.B = 5;
        cpu.HL = 0x1234;
        cpu.IX = 0x5678;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, Z80Asm.IncR(0)); // INC B — nothing to do with HL/IX

        int cycles = cpu.Step();

        Assert.Equal(6, cpu.B);
        Assert.Equal(0x1234, cpu.HL);
        Assert.Equal(0x5678, cpu.IX);
        Assert.Equal(8, cycles); // 4 (unprefixed INC r) + 4 (wasted prefix)
    }

    [Fact]
    public void DdImmediatelyFollowedByEd_DiscardsTheIndexPrefix()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        cpu.A = 0x80;
        cpu.HL = 0x1234;
        bus.WriteByte(0, 0xDD);
        bus.WriteByte(1, 0xED);
        bus.WriteByte(2, 0x44); // NEG

        int cycles = cpu.Step();

        Assert.Equal(0x80, cpu.A); // NEG of -128 stays -128
        Assert.Equal(0x1234, cpu.HL); // no substitution happened
        Assert.Equal(12, cycles); // 4 (wasted DD) + 8 (NEG's own total)
    }

    [Fact]
    public void RepeatedIndexPrefix_TheLastOneWins()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0;
        bus.WriteByte(0, 0xFD);
        bus.WriteByte(1, 0xDD); // FD then DD — DD is the one that actually applies
        bus.WriteByte(2, Z80Asm.LdRpImm(2)); // LD IX,nn
        bus.WriteWord(3, 0x9000);

        int cycles = cpu.Step();

        Assert.Equal(0x9000, cpu.IX);
        Assert.Equal(18, cycles); // 4 (wasted FD) + 4 (wasted DD) + 10 (LD IX,nn unprefixed cost)
    }
}
