using NesSharp.Cpu;
using Xunit;

namespace NesSharp.Tests;

public class Cpu6502Tests
{
    private static (Cpu6502 Cpu, FlatRamBus Bus) Boot(ushort start)
    {
        var bus = new FlatRamBus();
        bus.SetResetVector(start);
        var cpu = new Cpu6502(bus);
        cpu.Reset();
        int resetCycles = cpu.RunUntilBoundary();
        Assert.Equal(7, resetCycles);
        Assert.Equal(start, cpu.PC);
        return (cpu, bus);
    }

    [Fact]
    public void Lda_Immediate_SetsAccumulatorAndFlags_Takes2Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x42); // LDA #$42

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(2, cycles);
        Assert.Equal(0x42, cpu.A);
        Assert.False(cpu.GetFlag(CpuFlags.Zero));
        Assert.False(cpu.GetFlag(CpuFlags.Negative));
    }

    [Fact]
    public void Lda_Immediate_Zero_SetsZeroFlag()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x00);

        cpu.RunUntilBoundary();

        Assert.True(cpu.GetFlag(CpuFlags.Zero));
        Assert.False(cpu.GetFlag(CpuFlags.Negative));
    }

    [Fact]
    public void Lda_ZeroPageX_WrapsWithinZeroPage_Takes4Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA2, 0xFF); // LDX #$FF
        bus.Load(0x8002, 0xB5, 0x80); // LDA $80,X  -> effective addr (0x80+0xFF)&0xFF = 0x7F
        bus.Load(0x007F, 0x99);

        cpu.RunUntilBoundary(); // LDX
        int cycles = cpu.RunUntilBoundary(); // LDA

        Assert.Equal(4, cycles);
        Assert.Equal(0x99, cpu.A);
        Assert.True(cpu.GetFlag(CpuFlags.Negative));
    }

    [Fact]
    public void Sta_Absolute_WritesAccumulator()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x7A); // LDA #$7A
        bus.Load(0x8002, 0x8D, 0x00, 0x30); // STA $3000

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(4, cycles);
        Assert.Equal(0x7A, bus.Peek(0x3000));
    }

    [Fact]
    public void Lda_AbsoluteX_NoPageCross_Takes4Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA2, 0x10); // LDX #$10
        bus.Load(0x8002, 0xBD, 0x00, 0x20); // LDA $2000,X -> $2010, same page
        bus.Load(0x2010, 0x11);

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(4, cycles);
        Assert.Equal(0x11, cpu.A);
    }

    [Fact]
    public void Lda_AbsoluteX_PageCross_Takes5Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA2, 0x20); // LDX #$20
        bus.Load(0x8002, 0xBD, 0xF0, 0x20); // LDA $20F0,X -> $2110, crosses into next page
        bus.Load(0x2110, 0x22);

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(5, cycles);
        Assert.Equal(0x22, cpu.A);
    }

    [Theory]
    [InlineData((ushort)0x10, 5)] // $2000,X -> $2010, no page cross
    [InlineData((ushort)0x20, 5)] // $20F0,X analogue below crosses, cycle count is fixed regardless
    public void Sta_AbsoluteX_AlwaysTakesFixedCycles_RegardlessOfPageCross(ushort xValue, int expectedCycles)
    {
        var (cpu, bus) = Boot(0x8000);
        ushort baseAddr = xValue == 0x10 ? (ushort)0x2000 : (ushort)0x20F0;
        bus.Load(0x8000, 0xA2, (byte)xValue); // LDX #xValue
        bus.Load(0x8002, 0xA9, 0x5A); // LDA #$5A
        bus.Load(0x8004, 0x9D, (byte)(baseAddr & 0xFF), (byte)(baseAddr >> 8)); // STA base,X

        cpu.RunUntilBoundary(); // LDX
        cpu.RunUntilBoundary(); // LDA
        int cycles = cpu.RunUntilBoundary(); // STA

        Assert.Equal(expectedCycles, cycles);
        Assert.Equal(0x5A, bus.Peek((ushort)(baseAddr + xValue)));
    }

    [Fact]
    public void Adc_SetsCarryOverflowNegative_OnSignedOverflow()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x18); // CLC
        bus.Load(0x8001, 0xA9, 0x50); // LDA #$50
        bus.Load(0x8003, 0x69, 0x60); // ADC #$60

        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();

        Assert.Equal(0xB0, cpu.A);
        Assert.False(cpu.GetFlag(CpuFlags.Carry));
        Assert.True(cpu.GetFlag(CpuFlags.Overflow));
        Assert.True(cpu.GetFlag(CpuFlags.Negative));
    }

    [Fact]
    public void Sbc_NoBorrow_ClearsFlagsAsExpected()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x38); // SEC (carry set = no borrow going in)
        bus.Load(0x8001, 0xA9, 0x50); // LDA #$50
        bus.Load(0x8003, 0xE9, 0x10); // SBC #$10

        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();

        Assert.Equal(0x40, cpu.A);
        Assert.True(cpu.GetFlag(CpuFlags.Carry)); // no borrow occurred
    }

    [Fact]
    public void Branch_NotTaken_Takes2Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xF0, 0x10); // BEQ +$10
        cpu.SetFlag(CpuFlags.Zero, false);

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(2, cycles);
        Assert.Equal(0x8002, cpu.PC);
    }

    [Fact]
    public void Branch_TakenSamePage_Takes3Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xF0, 0x10); // BEQ +$10
        cpu.SetFlag(CpuFlags.Zero, true);

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(3, cycles);
        Assert.Equal(0x8012, cpu.PC);
    }

    [Fact]
    public void Branch_TakenCrossingPage_Takes4Cycles()
    {
        var (cpu, bus) = Boot(0x800E);
        bus.Load(0x800E, 0xF0, 0xE0); // BEQ -$20; PC after operand fetch = $8010, target = $7FF0
        cpu.SetFlag(CpuFlags.Zero, true);

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(4, cycles);
        Assert.Equal(0x7FF0, cpu.PC);
    }

    [Fact]
    public void Jsr_Rts_RoundTrip_ReturnsToAddressAfterCall()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x20, 0x00, 0x90); // JSR $9000
        bus.Load(0x9000, 0x60); // RTS

        int jsrCycles = cpu.RunUntilBoundary();
        Assert.Equal(6, jsrCycles);
        Assert.Equal(0x9000, cpu.PC);

        int rtsCycles = cpu.RunUntilBoundary();
        Assert.Equal(6, rtsCycles);
        Assert.Equal(0x8003, cpu.PC);
    }

    [Fact]
    public void Pha_Pla_RoundTrip_RestoresAccumulator()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x55); // LDA #$55
        bus.Load(0x8002, 0x48); // PHA
        bus.Load(0x8003, 0xA9, 0x00); // LDA #$00
        bus.Load(0x8005, 0x68); // PLA

        cpu.RunUntilBoundary(); // LDA #$55
        int phaCycles = cpu.RunUntilBoundary();
        cpu.RunUntilBoundary(); // LDA #$00
        int plaCycles = cpu.RunUntilBoundary();

        Assert.Equal(3, phaCycles);
        Assert.Equal(4, plaCycles);
        Assert.Equal(0x55, cpu.A);
        Assert.False(cpu.GetFlag(CpuFlags.Zero));
    }

    [Fact]
    public void Brk_PushesStateAndJumpsToIrqVector_Takes7Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.SetIrqVector(0x9000);
        bus.Load(0x8000, 0x00); // BRK

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(7, cycles);
        Assert.Equal(0x9000, cpu.PC);
        Assert.True(cpu.GetFlag(CpuFlags.InterruptDisable));
    }

    [Fact]
    public void JmpIndirect_ReproducesPageBoundaryBug()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x6C, 0xFF, 0x20); // JMP ($20FF)
        bus.Load(0x20FF, 0x34); // low byte of target
        bus.Load(0x2000, 0x12); // high byte incorrectly re-read from $2000, not $2100
        bus.Load(0x2100, 0x99); // if the bug were absent, this would be used instead

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(5, cycles);
        Assert.Equal(0x1234, cpu.PC);
    }

    [Fact]
    public void Inx_WrapsAndSetsZeroFlag_Takes2Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA2, 0xFF); // LDX #$FF
        bus.Load(0x8002, 0xE8); // INX

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(2, cycles);
        Assert.Equal(0x00, cpu.X);
        Assert.True(cpu.GetFlag(CpuFlags.Zero));
    }

    [Fact]
    public void Asl_Accumulator_ShiftsAndSetsCarry_Takes2Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x81); // LDA #$81
        bus.Load(0x8002, 0x0A); // ASL A

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(2, cycles);
        Assert.Equal(0x02, cpu.A);
        Assert.True(cpu.GetFlag(CpuFlags.Carry));
    }

    [Fact]
    public void UnimplementedUnstableOpcode_ThrowsUnimplementedOpcodeException()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x8B); // ANE/XAA — unstable, analog-dependent, intentionally unimplemented

        var ex = Assert.Throws<UnimplementedOpcodeException>(() => cpu.RunUntilBoundary());
        Assert.Equal(0x8B, ex.Opcode);
    }

    [Fact]
    public void JamOpcode_FreezesCpu_UntilReset()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x02); // JAM/KIL

        cpu.RunUntilBoundary();
        Assert.True(cpu.Jammed);
        ushort pcWhenJammed = cpu.PC;

        // Keep clocking: it must never advance past the jam, one dummy read per cycle.
        for (int i = 0; i < 20; i++)
        {
            cpu.Clock();
        }
        Assert.True(cpu.Jammed);
        Assert.Equal(pcWhenJammed, cpu.PC);

        cpu.Reset();
        Assert.False(cpu.Jammed);
    }

    [Fact]
    public void Lax_LoadsBothAccumulatorAndX()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA7, 0x10); // LAX $10
        bus.Load(0x0010, 0x77);

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(3, cycles);
        Assert.Equal(0x77, cpu.A);
        Assert.Equal(0x77, cpu.X);
    }

    [Fact]
    public void Sax_StoresAccumulatorAndXBitwiseAnd()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0xF0); // LDA #$F0
        bus.Load(0x8002, 0xA2, 0x3C); // LDX #$3C
        bus.Load(0x8004, 0x87, 0x20); // SAX $20

        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(3, cycles);
        Assert.Equal(0x30, bus.Peek(0x0020)); // 0xF0 & 0x3C
    }

    [Fact]
    public void Dcp_DecrementsMemoryAndComparesAgainstAccumulator_Takes5Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x10); // LDA #$10
        bus.Load(0x8002, 0xC7, 0x20); // DCP $20
        bus.Load(0x0020, 0x10);

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(5, cycles);
        Assert.Equal(0x0F, bus.Peek(0x0020));
        Assert.True(cpu.GetFlag(CpuFlags.Carry)); // A (0x10) >= decremented value (0x0F)
        Assert.False(cpu.GetFlag(CpuFlags.Zero));
    }

    [Fact]
    public void Isc_IncrementsMemoryThenSubtractsFromAccumulator_Takes7Cycles()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0x38); // SEC
        bus.Load(0x8001, 0xA9, 0x10); // LDA #$10
        bus.Load(0x8003, 0xEF, 0x00, 0x30); // ISC $3000 (absolute)
        bus.Load(0x3000, 0x05);

        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(6, cycles);
        Assert.Equal(0x06, bus.Peek(0x3000)); // memory incremented
        Assert.Equal(0x0A, cpu.A); // 0x10 - 0x06 (post-increment value)
    }

    [Fact]
    public void Slo_ShiftsMemoryThenOrsIntoAccumulator()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x01); // LDA #$01
        bus.Load(0x8002, 0x07, 0x20); // SLO $20
        bus.Load(0x0020, 0x81);

        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();

        Assert.Equal(0x02, bus.Peek(0x0020)); // 0x81 << 1 = 0x02, carry out
        Assert.Equal(0x03, cpu.A); // 0x01 | 0x02
        Assert.True(cpu.GetFlag(CpuFlags.Carry));
    }

    [Fact]
    public void DcpIndirectY_Modify_Takes8CyclesRegardlessOfPageCross()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0x00); // LDA #$00
        bus.Load(0x8002, 0xA0, 0xFF); // LDY #$FF
        bus.Load(0x8004, 0xD3, 0x10); // DCP ($10),Y
        bus.Load(0x0010, 0x00);
        bus.Load(0x0011, 0x20); // pointer -> $2000, effective addr $2000+Y(0xFF) = $20FF
        bus.Load(0x20FF, 0x05);

        cpu.RunUntilBoundary(); // LDA
        cpu.RunUntilBoundary(); // LDY
        int cycles = cpu.RunUntilBoundary(); // DCP

        Assert.Equal(8, cycles);
        Assert.Equal(0x04, bus.Peek(0x20FF));
    }

    [Fact]
    public void AncImmediate_SetsCarryFromResultBit7()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0xFF); // LDA #$FF
        bus.Load(0x8002, 0x0B, 0x81); // ANC #$81

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(2, cycles);
        Assert.Equal(0x81, cpu.A);
        Assert.True(cpu.GetFlag(CpuFlags.Carry));
        Assert.True(cpu.GetFlag(CpuFlags.Negative));
    }

    [Fact]
    public void SbxImmediate_SubtractsFromAAndXBitwiseAnd()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA9, 0xFF); // LDA #$FF
        bus.Load(0x8002, 0xA2, 0x0F); // LDX #$0F
        bus.Load(0x8004, 0xCB, 0x05); // SBX #$05

        cpu.RunUntilBoundary();
        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(2, cycles);
        Assert.Equal(0x0A, cpu.X); // (0xFF & 0x0F) - 0x05 = 0x0F - 0x05
        Assert.True(cpu.GetFlag(CpuFlags.Carry));
    }

    [Theory]
    [InlineData((byte)0x1A, 2)] // implied NOP variant
    [InlineData((byte)0x04, 3)] // zero-page NOP variant
    public void UndocumentedNop_MatchesExpectedCycleCount(byte opcode, int expectedCycles)
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, opcode, 0x00); // second byte is a harmless operand/filler

        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(expectedCycles, cycles);
    }

    [Fact]
    public void UndocumentedNop_AbsoluteX_TakesExtraCycleOnPageCross()
    {
        var (cpu, bus) = Boot(0x8000);
        bus.Load(0x8000, 0xA2, 0x20); // LDX #$20
        bus.Load(0x8002, 0x1C, 0xF0, 0x20); // NOP $20F0,X -> crosses page

        cpu.RunUntilBoundary();
        int cycles = cpu.RunUntilBoundary();

        Assert.Equal(5, cycles);
    }

    [Fact]
    public void OpcodeTable_HasExactlyTheOfficialAndStableIllegalEntries()
    {
        // 141 official table-driven entries + 85 stable illegal opcodes. The remaining
        // 256 - 141 - 85 - 10 (control-flow special cases) - 12 (JAM) = 8 opcodes are the
        // unstable illegal ones, intentionally left unimplemented.
        int count = OpcodeTable.Table.Count(o => o.HasValue);
        Assert.Equal(226, count);
    }
}
