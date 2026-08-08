using GenesisSharp.CpuSh2;

namespace GenesisSharp.Tests;

public class Sh2Tests
{
    private static (Sh2 Cpu, FlatSh2Bus Bus) CreateCpu()
    {
        var bus = new FlatSh2Bus();
        var cpu = new Sh2(bus);
        return (cpu, bus);
    }

    [Fact]
    public void Reset_LoadsPcAndSpFromFixedAddressesAndMasksAllInterrupts()
    {
        var (cpu, bus) = CreateCpu();
        bus.WriteLong(0x0000_0000, 0x0000_1000); // PC vector
        bus.WriteLong(0x0000_0004, 0x0000_2000); // R15 (SP) vector

        cpu.Reset();

        Assert.Equal(0x0000_1000u, cpu.PC);
        Assert.Equal(0x0000_2000u, cpu.R[15]);
        Assert.Equal(0u, cpu.VBR);
        Assert.Equal(15, cpu.InterruptMask); // SR = 0xF0 -- all maskable interrupts blocked
    }

    [Fact]
    public void Step_Nop_AdvancesPcByTwoAndCostsOneCycle()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset(); // blank bus -> PC and R15 both load as 0
        bus.WriteWord(cpu.PC, Sh2Asm.Nop());

        int cycles = cpu.Step();

        Assert.Equal(1, cycles);
        Assert.Equal(2u, cpu.PC);
    }

    [Fact]
    public void Step_MovRegisterToRegister_CopiesValue()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        bus.WriteWord(cpu.PC, Sh2Asm.Mov(m: 3, n: 5));
        cpu.R[3] = 0x1234_5678;

        cpu.Step();

        Assert.Equal(0x1234_5678u, cpu.R[5]);
    }

    [Fact]
    public void Step_MovImmediate_SignExtendsToThirtyTwoBits()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        bus.WriteWord(cpu.PC, Sh2Asm.MovI(unchecked((byte)-5), n: 2));

        cpu.Step();

        Assert.Equal(unchecked((uint)-5), cpu.R[2]);
    }

    [Fact]
    public void Step_MovLongStoreThenLoad_RoundTripsThroughMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x1000;
        cpu.R[1] = 0xCAFEBABE;
        bus.WriteWord(cpu.PC, Sh2Asm.MovLS(m: 1, n: 0)); // MOV.L R1,@R0
        bus.WriteWord((ushort)(cpu.PC + 2), Sh2Asm.MovLL(m: 0, n: 2)); // MOV.L @R0,R2

        cpu.Step();
        cpu.Step();

        Assert.Equal(0xCAFEBABEu, bus.ReadLong(0x1000));
        Assert.Equal(0xCAFEBABEu, cpu.R[2]);
    }

    [Fact]
    public void Step_MovBytePredecrementStore_DecrementsBeforeWriting_EvenWhenSourceAndDestAreTheSameRegister()
    {
        // Regression test for the exact ordering PicoDrive's own comment calls out ("bug fix,
        // was reading sh2->r[n]"): the source value must be captured before the pointer register
        // is decremented, since Rm and Rn can alias.
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[4] = 0x2000_0042;
        bus.WriteWord(cpu.PC, Sh2Asm.MovBM(m: 4, n: 4));

        cpu.Step();

        Assert.Equal(0x2000_0041u, cpu.R[4]); // decremented by 1
        Assert.Equal(0x42, bus.ReadByte(0x2000_0041)); // the ORIGINAL value's low byte, not R4 post-decrement
    }

    [Fact]
    public void Step_MovWordPostincrementLoad_LeavesPointerAloneWhenSourceAndDestAreTheSameRegister()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[3] = 0x3000;
        bus.WriteWord(0x3000, 0x0007);
        bus.WriteWord(cpu.PC, Sh2Asm.MovWP(m: 3, n: 3));

        cpu.Step();

        Assert.Equal(7u, cpu.R[3]); // loaded value, not the incremented pointer
    }

    [Theory]
    [InlineData(5, 5, true)]
    [InlineData(5, 6, false)]
    public void Step_CmpEq_SetsTCorrectly(uint a, uint b, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = a;
        cpu.R[1] = b;
        bus.WriteWord(cpu.PC, Sh2Asm.CmpEq(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(expectedT, cpu.FlagT);
    }

    [Fact]
    public void Step_Dt_DecrementsAndSetsTOnlyWhenResultIsZero()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 1;
        bus.WriteWord(cpu.PC, Sh2Asm.Dt(n: 0));
        bus.WriteWord((ushort)(cpu.PC + 2), Sh2Asm.Dt(n: 0));

        cpu.Step();
        Assert.Equal(0u, cpu.R[0]);
        Assert.True(cpu.FlagT);

        cpu.Step();
        Assert.Equal(0xFFFF_FFFFu, cpu.R[0]); // wraps, doesn't clamp
        Assert.False(cpu.FlagT);
    }

    // ---- Delay-slot branch mechanics -- the highest-risk, most novel part of this core ----

    [Fact]
    public void Step_Bra_ExecutesDelaySlotBeforeJumping_AndReturnsCombinedCycles()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        uint start = cpu.PC;
        // Displacement math is relative to PC as it stands AFTER BRA's own opcode fetch
        // (start+2), per real SH-2's "address of this instruction + 4" PC-relative convention:
        // target = (start+2) + 2*2 + 2 = start+8.
        bus.WriteWord(start, Sh2Asm.Bra(disp12: 2));
        bus.WriteWord((ushort)(start + 2), Sh2Asm.MovI(imm8: 42, n: 0)); // delay slot: R0 = 42

        int cycles = cpu.Step();

        Assert.Equal(42u, cpu.R[0]); // delay slot instruction really executed
        Assert.Equal(start + 8, cpu.PC); // branch target applied only after it
        Assert.Equal(3, cycles); // BRA's own 2 + MOV #imm,Rn's 1, combined into one Step()
    }

    [Fact]
    public void Step_Bsr_SetsPrToAddressAfterDelaySlot_NotAfterBsrItself()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        uint start = cpu.PC;
        bus.WriteWord(start, Sh2Asm.Bsr(disp12: 0));
        bus.WriteWord((ushort)(start + 2), Sh2Asm.Nop());

        cpu.Step();

        Assert.Equal(start + 4, cpu.PR); // address right after the delay slot
        Assert.Equal(start + 4, cpu.PC); // disp=0 -> target = start + 0 + 2 + 2 = start + 4, same as PR here
    }

    [Fact]
    public void Step_JsrThenRts_RoundTripsThroughASubroutineCall()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        uint start = cpu.PC;
        uint subroutine = start + 0x100;

        cpu.R[1] = subroutine;
        bus.WriteWord(start, Sh2Asm.Jsr(n: 1));
        bus.WriteWord((ushort)(start + 2), Sh2Asm.Nop()); // JSR's delay slot
        bus.WriteWord(subroutine, Sh2Asm.Rts());
        bus.WriteWord((ushort)(subroutine + 2), Sh2Asm.MovI(imm8: 9, n: 2)); // RTS's delay slot

        cpu.Step(); // JSR + its delay slot -> PC = subroutine
        Assert.Equal(subroutine, cpu.PC);
        Assert.Equal(start + 4, cpu.PR);

        cpu.Step(); // RTS + its delay slot -> PC back to start+4, R2 set by the delay slot
        Assert.Equal(start + 4, cpu.PC);
        Assert.Equal(9u, cpu.R[2]);
    }

    [Fact]
    public void Step_Bts_AlwaysConsumesDelaySlot_EvenWhenBranchIsNotTaken()
    {
        // The single easiest mistake in this core to get backwards -- BT.S must execute the
        // delay-slot instruction whether or not T is set, unlike plain (non-delayed) BT.
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagT = false; // branch will NOT be taken
        uint start = cpu.PC;
        bus.WriteWord(start, Sh2Asm.Bts(disp8: 10));
        bus.WriteWord((ushort)(start + 2), Sh2Asm.MovI(imm8: 77, n: 3)); // delay slot

        int cycles = cpu.Step();

        Assert.Equal(77u, cpu.R[3]); // delay slot still executed
        Assert.Equal(start + 4, cpu.PC); // plain fallthrough past the delay slot, branch not taken
        Assert.Equal(2, cycles); // BTS not-taken (1) + MOV #imm (1)
    }

    [Fact]
    public void Step_Bts_JumpsToDisplacementTarget_WhenTaken()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagT = true;
        uint start = cpu.PC;
        // taken target = (start+2) + 3*2 + 2 = start+10 (same "+2" convention as BT/BRA; BTS's
        // own body applies it in two steps internally, but the net displacement math matches).
        bus.WriteWord(start, Sh2Asm.Bts(disp8: 3));
        bus.WriteWord((ushort)(start + 2), Sh2Asm.Nop());

        int cycles = cpu.Step();

        Assert.Equal(start + 10, cpu.PC);
        Assert.Equal(3, cycles); // BTS taken (2) + NOP (1)
    }

    [Fact]
    public void Step_Bt_IsNotDelayed_TargetTakesEffectImmediatelyWithNoSlot()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagT = true;
        uint start = cpu.PC;
        // target = (start+2) + 3*2 + 2 = start+10.
        bus.WriteWord(start, Sh2Asm.Bt(disp8: 3));
        cpu.R[0] = 0;
        bus.WriteWord((ushort)(start + 10), Sh2Asm.MovI(imm8: 1, n: 0));
        // Deliberately leave start+2 as an illegal/garbage word (0x0000, which decodes as
        // ILLEGAL) to prove it is never fetched -- BT has no delay slot to execute.

        int cycles = cpu.Step();

        Assert.Equal(start + 10, cpu.PC);
        Assert.Equal(3, cycles); // taken: base 1 + extra 2, no slot instruction folded in
    }

    [Fact]
    public void Step_IllegalOpcode_PushesOwnAddressAndJumpsToVectorFour()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.VBR = 0x0000_4000;
        cpu.R[15] = 0x0000_9000;
        bus.WriteLong(cpu.VBR + 4 * 4, 0x0000_5000); // vector 4 handler address
        uint illegalOpcodeAddress = cpu.PC;
        bus.WriteWord(illegalOpcodeAddress, 0x0000); // 0x0000 decodes as ILLEGAL

        int cycles = cpu.Step();

        Assert.Equal(0x0000_5000u, cpu.PC);
        Assert.Equal(6, cycles);
        Assert.Equal(illegalOpcodeAddress, bus.ReadLong(cpu.R[15])); // pushed PC = the illegal opcode's OWN address
    }

    [Fact]
    public void Step_Rte_RestoresPcAndSrImmediately_BeforeTheDelaySlotRuns()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[15] = 0x0000_8000;
        uint returnAddress = 0x0000_3000;
        bus.WriteLong(cpu.R[15], returnAddress); // popped PC
        bus.WriteLong(cpu.R[15] + 4, 0x0000_0001); // popped SR: T=1, nothing else
        bus.WriteWord(cpu.PC, Sh2Asm.Rte());
        // A flag-independent delay-slot instruction -- proves both that RTE's own SR/PC
        // updates aren't deferred by the delay-slot mechanism (only the PC *jump itself* is,
        // same as every other delayed branch) and that the slot instruction really executes.
        bus.WriteWord((ushort)(cpu.PC + 2), Sh2Asm.MovI(imm8: 5, n: 4));

        cpu.FlagT = false; // pre-RTE state -- must not be what ends up observable after Step()
        cpu.Step();

        Assert.Equal(returnAddress, cpu.PC); // RTE's target is the popped PC directly, no displacement math
        Assert.True(cpu.FlagT); // restored by the popped SR
        Assert.Equal(5u, cpu.R[4]); // delay slot really executed
        Assert.Equal(0x0000_8008u, cpu.R[15]); // two longs popped
    }

    [Fact]
    public void Step_NestedBranchInDelaySlot_RaisesIllegalSlotInstructionAtVectorSix()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.VBR = 0x0000_4000;
        cpu.R[15] = 0x0000_9000;
        bus.WriteLong(cpu.VBR + 6 * 4, 0x0000_6000); // vector 6 handler address
        uint start = cpu.PC;
        bus.WriteWord(start, Sh2Asm.Bra(disp12: 0));
        bus.WriteWord((ushort)(start + 2), Sh2Asm.Bra(disp12: 0)); // illegal: BRA in a delay slot

        cpu.Step();

        Assert.Equal(0x0000_6000u, cpu.PC);
    }

    [Fact]
    public void Step_IllegalOpcode_PushesSrAndPcAndJumpsToVectorFour()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.VBR = 0x0000_4000;
        cpu.R[15] = 0x0000_9000;
        cpu.FlagT = true;
        bus.WriteLong(cpu.VBR + 4 * 4, 0x0000_5000); // vector 4 handler address
        uint start = cpu.PC;
        bus.WriteWord(start, 0x0000); // 0000000000000000 is ILLEGAL on real hardware

        int cycles = cpu.Step();

        Assert.Equal(0x0000_5000u, cpu.PC);
        Assert.Equal(0x0000_8FF8u, cpu.R[15]); // two longs pushed (SR first, then PC)
        Assert.Equal(start, bus.ReadLong(cpu.R[15])); // PC pushed is the illegal opcode's own address
        Assert.Equal(cpu.SR, bus.ReadLong(cpu.R[15] + 4)); // SR pushed unmodified
        Assert.Equal(6, cycles);
    }

    // ---- Shifts and rotates ----

    [Theory]
    [InlineData(0x8000_0000u, 0u, true)]
    [InlineData(0x4000_0000u, 0x8000_0000u, false)]
    public void Step_Shll_ShiftsLeftAndSetsTFromTheBitShiftedOut(uint before, uint after, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = before;
        bus.WriteWord(cpu.PC, Sh2Asm.Shll(n: 0));

        cpu.Step();

        Assert.Equal(after, cpu.R[0]);
        Assert.Equal(expectedT, cpu.FlagT);
    }

    [Fact]
    public void Step_Shar_PreservesSignBitUnlikeShlr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x8000_0004; // negative
        bus.WriteWord(cpu.PC, Sh2Asm.Shar(n: 0));

        cpu.Step();

        Assert.Equal(0xC000_0002u, cpu.R[0]); // sign-extended, not zero-filled
        Assert.False(cpu.FlagT); // bit 0 (the bit shifted out) was 0
    }

    [Fact]
    public void Step_Shll2_8_16_DoNotTouchTFlag()
    {
        // Unlike SHLL's single-bit form, the fixed-count variants leave T alone entirely --
        // easy to assume is a transcription slip rather than the real distinction it is.
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 1;
        cpu.FlagT = true;
        bus.WriteWord(cpu.PC, Sh2Asm.Shll2(n: 0));

        cpu.Step();

        Assert.Equal(4u, cpu.R[0]);
        Assert.True(cpu.FlagT); // unchanged
    }

    [Fact]
    public void Step_Rotcl_ShiftsInOldTAndCapturesNewTFromBitThirtyOne()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x8000_0000;
        cpu.FlagT = true;
        bus.WriteWord(cpu.PC, Sh2Asm.Rotcl(n: 0));

        cpu.Step();

        Assert.Equal(1u, cpu.R[0]); // old bit31 rotated out, old T (1) rotated in at bit 0
        Assert.True(cpu.FlagT); // new T = the bit31 that was rotated out
    }

    [Fact]
    public void Step_Rotl_RotatesTopBitBackToBottom()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x8000_0001;
        bus.WriteWord(cpu.PC, Sh2Asm.Rotl(n: 0));

        cpu.Step();

        Assert.Equal(3u, cpu.R[0]);
        Assert.True(cpu.FlagT);
    }

    // ---- Logic ----

    [Fact]
    public void Step_And_MasksRegisters()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0xFF00_FF00;
        cpu.R[1] = 0x0FF0_0FF0;
        bus.WriteWord(cpu.PC, Sh2Asm.And(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x0F00_0F00u, cpu.R[0]);
    }

    [Fact]
    public void Step_Tst_SetsTWhenAndResultIsZero()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x0F0F;
        cpu.R[1] = 0xF0F0;
        bus.WriteWord(cpu.PC, Sh2Asm.Tst(m: 1, n: 0));

        cpu.Step();

        Assert.True(cpu.FlagT);
    }

    [Fact]
    public void Step_Not_ComplementsAllBits()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_FFFFu;
        bus.WriteWord(cpu.PC, Sh2Asm.Not(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0xFFFF_0000u, cpu.R[0]);
    }

    [Fact]
    public void Step_AndImmediate_UsesR0AndDoesNotSignExtend()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0xFFFF_FFFF;
        bus.WriteWord(cpu.PC, Sh2Asm.AndI(imm8: 0x80)); // if sign-extended this would clear the top bits too

        cpu.Step();

        Assert.Equal(0x0000_0080u, cpu.R[0]);
    }

    [Fact]
    public void Step_OrGbrMemory_ReadModifyWritesASingleByteAtGbrPlusR0()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.GBR = 0x2000;
        cpu.R[0] = 0x10;
        bus.WriteByte(0x2010, 0x0F);
        bus.WriteWord(cpu.PC, Sh2Asm.OrM(imm8: 0xF0));

        int cycles = cpu.Step();

        Assert.Equal(0xFF, bus.ReadByte(0x2010));
        Assert.Equal(3, cycles); // base 1 + PicoDrive's own extra 2 for the memory read-modify-write
    }

    [Fact]
    public void Step_XorGbrMemory_ReadModifyWritesASingleByte()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.GBR = 0x2000;
        cpu.R[0] = 0x10;
        bus.WriteByte(0x2010, 0xFF);
        bus.WriteWord(cpu.PC, Sh2Asm.XorM(imm8: 0x0F));

        cpu.Step();

        Assert.Equal(0xF0, bus.ReadByte(0x2010));
    }

    // ---- Multiply / MAC ----

    [Fact]
    public void Step_Mull_TruncatesToLowThirtyTwoBits()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x0001_0000;
        cpu.R[1] = 0x0001_0000; // product = 0x1_0000_0000, low 32 bits = 0
        bus.WriteWord(cpu.PC, Sh2Asm.Mull(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(0u, cpu.MACL);
        Assert.Equal(2, cycles);
    }

    [Fact]
    public void Step_Muls_SignExtendsBothOperandsToSixteenBits()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = unchecked((uint)-2); // low 16 bits: 0xFFFE = -2 as int16
        cpu.R[1] = 3;
        bus.WriteWord(cpu.PC, Sh2Asm.Muls(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(unchecked((uint)-6), cpu.MACL);
    }

    [Fact]
    public void Step_Mulu_TreatsBothOperandsAsUnsignedSixteenBit()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0xFFFE; // as unsigned 16-bit: 65534, NOT -2
        cpu.R[1] = 3;
        bus.WriteWord(cpu.PC, Sh2Asm.Mulu(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(65534u * 3u, cpu.MACL);
    }

    [Fact]
    public void Step_Dmuls_ProducesFullSixtyFourBitSignedProduct()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = unchecked((uint)-1000);
        cpu.R[1] = 2000;
        bus.WriteWord(cpu.PC, Sh2Asm.Dmuls(m: 1, n: 0));

        int cycles = cpu.Step();

        long expected = -1000L * 2000L;
        long actual = unchecked((long)(((ulong)cpu.MACH << 32) | cpu.MACL));
        Assert.Equal(expected, actual);
        Assert.Equal(2, cycles);
    }

    [Fact]
    public void Step_Dmulu_ProducesFullSixtyFourBitUnsignedProduct()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0xFFFF_FFFF;
        cpu.R[1] = 2;
        bus.WriteWord(cpu.PC, Sh2Asm.Dmulu(m: 1, n: 0));

        cpu.Step();

        ulong expected = 0xFFFF_FFFFUL * 2UL;
        ulong actual = ((ulong)cpu.MACH << 32) | cpu.MACL;
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Step_MacL_ReadsBothOperandsPostIncrementingByFour_AndAccumulates()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x1000;
        cpu.R[1] = 0x2000;
        bus.WriteLong(0x1000, unchecked((uint)3));
        bus.WriteLong(0x2000, unchecked((uint)4));
        cpu.MACH = 0;
        cpu.MACL = 5;
        bus.WriteWord(cpu.PC, Sh2Asm.MacL(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(0x1004u, cpu.R[0]);
        Assert.Equal(0x2004u, cpu.R[1]);
        Assert.Equal(17u, cpu.MACL); // 5 + 3*4
        Assert.Equal(0u, cpu.MACH);
        Assert.Equal(3, cycles);
    }

    [Fact]
    public void Step_MacL_SaturatesToFortyEightBitMaxWhenSFlagSet()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagS = true;
        cpu.R[0] = 0x1000;
        cpu.R[1] = 0x2000;
        bus.WriteLong(0x1000, 0x7FFF_FFFF);
        bus.WriteLong(0x2000, 0x7FFF_FFFF); // huge positive product
        cpu.MACH = 0x7FFF; // already near the 48-bit positive ceiling
        cpu.MACL = 0xFFFF_FFFF;
        bus.WriteWord(cpu.PC, Sh2Asm.MacL(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x0000_7FFFu, cpu.MACH);
        Assert.Equal(0xFFFF_FFFFu, cpu.MACL);
    }

    [Fact]
    public void Step_MacW_AccumulatesSignExtendedProductAcrossMachAndMacl_WhenSFlagClear()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagS = false;
        cpu.R[0] = 0x1000;
        cpu.R[1] = 0x2000;
        bus.WriteWord(0x1000, unchecked((ushort)(-5))); // -5
        bus.WriteWord(0x2000, 3);
        cpu.MACH = 0;
        cpu.MACL = 0;
        bus.WriteWord(cpu.PC, Sh2Asm.MacW(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(0x1002u, cpu.R[0]);
        Assert.Equal(0x2002u, cpu.R[1]);
        long acc = unchecked((long)(((ulong)cpu.MACH << 32) | cpu.MACL));
        Assert.Equal(-15L, acc); // -5 * 3, sign-extended into a 64-bit accumulate
        Assert.Equal(3, cycles);
    }

    [Fact]
    public void Step_MacW_SaturatesMaclAloneWhenSFlagSet_MachUntouched()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagS = true;
        cpu.R[0] = 0x1000;
        cpu.R[1] = 0x2000;
        bus.WriteWord(0x1000, 0x7FFF); // max positive int16
        bus.WriteWord(0x2000, 0x7FFF);
        cpu.MACH = 0x1234_5678; // must stay untouched -- MAC.W's saturation mode never writes MACH
        cpu.MACL = 0x7FFF_FFFF; // already at the positive ceiling

        bus.WriteWord(cpu.PC, Sh2Asm.MacW(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x7FFF_FFFFu, cpu.MACL); // clamped, not wrapped
        Assert.Equal(0x1234_5678u, cpu.MACH); // untouched
    }

    // ---- Divide ----

    [Fact]
    public void Step_Div0u_ClearsQMAndT()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagQ = true;
        cpu.FlagM = true;
        cpu.FlagT = true;
        bus.WriteWord(cpu.PC, Sh2Asm.Div0u());

        int cycles = cpu.Step();

        Assert.False(cpu.FlagQ);
        Assert.False(cpu.FlagM);
        Assert.False(cpu.FlagT);
        Assert.Equal(1, cycles);
    }

    [Theory]
    [InlineData(0x0000_0001u, 0x0000_0001u, false, false, false)] // both positive -> same sign -> T=0
    [InlineData(0x8000_0000u, 0x0000_0001u, true, false, true)]   // Rn negative, Rm positive -> differ -> T=1
    [InlineData(0x0000_0001u, 0x8000_0000u, false, true, true)]   // Rn positive, Rm negative -> differ -> T=1
    [InlineData(0x8000_0000u, 0x8000_0000u, true, true, false)]   // both negative -> same sign -> T=0
    public void Step_Div0s_SeedsQMAndTFromOperandSigns(uint rn, uint rm, bool expectedQ, bool expectedM, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = rn;
        cpu.R[1] = rm;
        bus.WriteWord(cpu.PC, Sh2Asm.Div0s(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(expectedQ, cpu.FlagQ);
        Assert.Equal(expectedM, cpu.FlagM);
        Assert.Equal(expectedT, cpu.FlagT);
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_Div1_MatchesIndependentBitwiseReferenceAcrossRandomizedOperands()
    {
        // DIV1's Q/M/T bookkeeping (cpu/sh2/mame/sh2.c:627-716) is exactly the kind of subtle
        // carry logic this codebase's own culture treats as too easy to get wrong via a hand
        // re-derivation alone (see this file's own Sh2.Divide.cs remarks) -- in fact a hand
        // re-derivation pass over this very method caught one inverted branch before this test
        // was written. ReferenceDiv1 below is a second, independent transcription of the same C
        // source (raw SR-bit arithmetic instead of the production code's FlagQ/FlagM/FlagT bool
        // properties) so a systematic transcription mistake shared by both wouldn't be masked by
        // testing the production code against itself.
        var rng = new Random(12345);
        Span<byte> buf = stackalloc byte[4];

        for (int i = 0; i < 500; i++)
        {
            rng.NextBytes(buf);
            uint rn = BitConverter.ToUInt32(buf);
            rng.NextBytes(buf);
            uint rm = BitConverter.ToUInt32(buf);
            bool q = rng.Next(2) == 1;
            bool m = rng.Next(2) == 1;
            bool t = rng.Next(2) == 1;

            var (cpu, bus) = CreateCpu();
            cpu.Reset();
            cpu.R[0] = rn;
            cpu.R[1] = rm;
            cpu.FlagQ = q;
            cpu.FlagM = m;
            cpu.FlagT = t;
            bus.WriteWord(cpu.PC, Sh2Asm.Div1(m: 1, n: 0));
            cpu.Step();

            uint sr = (q ? 0x100u : 0u) | (m ? 0x200u : 0u) | (t ? 0x1u : 0u);
            (uint expectedRn, uint expectedSr) = ReferenceDiv1(rn, rm, sr);

            Assert.Equal(expectedRn, cpu.R[0]);
            Assert.Equal((expectedSr & 0x100) != 0, cpu.FlagQ);
            Assert.Equal((expectedSr & 0x200) != 0, cpu.FlagM);
            Assert.Equal((expectedSr & 0x1) != 0, cpu.FlagT);
        }
    }

    /// <summary>Independent transcription of PicoDrive's DIV1 (cpu/sh2/mame/sh2.c:627-716) using
    /// raw SR-bit arithmetic, deliberately structured differently from Sh2.Divide.cs's
    /// production implementation, for the cross-check above.</summary>
    private static (uint rn, uint sr) ReferenceDiv1(uint rn, uint rm, uint sr)
    {
        const uint Q = 0x100, M = 0x200, T = 0x1;
        uint oldQ = sr & Q;

        if ((rn & 0x8000_0000) != 0) sr |= Q; else sr &= ~Q;
        rn = (rn << 1) | (sr & T);

        uint tmp0;
        if (oldQ == 0)
        {
            if ((sr & M) == 0)
            {
                tmp0 = rn;
                rn = unchecked(rn - rm);
                if ((sr & Q) == 0) { if (rn > tmp0) sr |= Q; else sr &= ~Q; }
                else { if (rn > tmp0) sr &= ~Q; else sr |= Q; }
            }
            else
            {
                tmp0 = rn;
                rn = unchecked(rn + rm);
                if ((sr & Q) == 0) { if (rn < tmp0) sr &= ~Q; else sr |= Q; }
                else { if (rn < tmp0) sr |= Q; else sr &= ~Q; }
            }
        }
        else
        {
            if ((sr & M) == 0)
            {
                tmp0 = rn;
                rn = unchecked(rn + rm);
                if ((sr & Q) == 0) { if (rn < tmp0) sr |= Q; else sr &= ~Q; }
                else { if (rn < tmp0) sr &= ~Q; else sr |= Q; }
            }
            else
            {
                tmp0 = rn;
                rn = unchecked(rn - rm);
                if ((sr & Q) == 0) { if (rn > tmp0) sr &= ~Q; else sr |= Q; }
                else { if (rn > tmp0) sr |= Q; else sr &= ~Q; }
            }
        }

        tmp0 = sr & (Q | M);
        if (tmp0 == 0 || tmp0 == (Q | M)) sr |= T; else sr &= ~T;

        return (rn, sr);
    }

    // ---- Sub / overflow arithmetic ----

    [Fact]
    public void Step_Sub_SubtractsWithNoFlagsAffected()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 10;
        cpu.R[1] = 3;
        bus.WriteWord(cpu.PC, Sh2Asm.Sub(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(7u, cpu.R[0]);
        Assert.Equal(1, cycles);
    }

    [Theory]
    [InlineData(5u, 2u, false, 3u, false)] // plain subtract, no borrow anywhere
    [InlineData(0u, 1u, false, 0xFFFF_FFFFu, true)] // the plain subtract itself borrows
    [InlineData(0u, 0u, true, 0xFFFF_FFFFu, true)] // only the borrow-in wraps -- the case ExecuteAddc's mirror-image bug would miss
    public void Step_Subc_SubtractsWithBorrowInAndOut(uint rn, uint rm, bool carryIn, uint expectedRn, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = rn;
        cpu.R[1] = rm;
        cpu.FlagT = carryIn;
        bus.WriteWord(cpu.PC, Sh2Asm.Subc(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(expectedRn, cpu.R[0]);
        Assert.Equal(expectedT, cpu.FlagT);
    }

    [Theory]
    [InlineData(1u, 1u, false, 2u, false)] // no carry anywhere
    [InlineData(0xFFFF_FFFFu, 1u, false, 0u, true)] // the plain add itself carries
    [InlineData(0xFFFF_FFFFu, 0u, true, 0u, true)] // regression: only the carry-in wraps (R[n]=-1,R[m]=0,T=1) -- the exact case a since-fixed version of ExecuteAddc got wrong by only checking the plain-add overflow
    public void Step_Addc_AddsWithCarryInAndOut(uint rn, uint rm, bool carryIn, uint expectedRn, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = rn;
        cpu.R[1] = rm;
        cpu.FlagT = carryIn;
        bus.WriteWord(cpu.PC, Sh2Asm.Addc(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(expectedRn, cpu.R[0]);
        Assert.Equal(expectedT, cpu.FlagT);
    }

    [Theory]
    [InlineData(1u, 1u, 2u, false)] // no overflow
    [InlineData(0x7FFF_FFFFu, 1u, 0x8000_0000u, true)] // INT_MAX + 1 -> signed overflow
    [InlineData(0x7FFF_FFFFu, 0x8000_0000u, 0xFFFF_FFFFu, false)] // mixed signs can never signed-overflow on add
    public void Step_Addv_AddsAndSetsTOnSignedOverflow(uint rn, uint rm, uint expectedRn, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = rn;
        cpu.R[1] = rm;
        bus.WriteWord(cpu.PC, Sh2Asm.Addv(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(expectedRn, cpu.R[0]);
        Assert.Equal(expectedT, cpu.FlagT);
    }

    [Theory]
    [InlineData(5u, 2u, 3u, false)] // no overflow
    [InlineData(0x8000_0000u, 1u, 0x7FFF_FFFFu, true)] // INT_MIN - 1 -> signed overflow
    [InlineData(5u, 0xFFFF_FFFFu, 6u, false)] // mixed signs (5 - (-1)): same-sign check fails, no overflow flagged
    public void Step_Subv_SubtractsAndSetsTOnSignedOverflow(uint rn, uint rm, uint expectedRn, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = rn;
        cpu.R[1] = rm;
        bus.WriteWord(cpu.PC, Sh2Asm.Subv(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(expectedRn, cpu.R[0]);
        Assert.Equal(expectedT, cpu.FlagT);
    }

    // ---- CmpStr / Xtrct / Clrmac / Sleep ----

    [Theory]
    [InlineData(0x1234_5678u, 0x1234_5678u, true)] // identical -- every byte lane matches
    [InlineData(0x1234_5678u, 0x0034_5678u, true)] // only the low byte lane matches (high XOR lane is 0x12)
    [InlineData(0x1234_5678u, 0xABCD_EF01u, false)] // every byte lane differs
    public void Step_CmpStr_SetsTWhenAnyByteLaneMatches(uint rn, uint rm, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = rn;
        cpu.R[1] = rm;
        bus.WriteWord(cpu.PC, Sh2Asm.CmpStr(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(expectedT, cpu.FlagT);
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_Xtrct_TakesRnHighHalfAndRmLowHalf()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x1111_2222u; // Rn
        cpu.R[1] = 0x3333_4444u; // Rm
        bus.WriteWord(cpu.PC, Sh2Asm.Xtrct(m: 1, n: 0));

        int cycles = cpu.Step();

        Assert.Equal(0x4444_1111u, cpu.R[0]); // Rm's low half : Rn's high half
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_Clrmac_ZeroesMachAndMacl()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.MACH = 0x1234;
        cpu.MACL = 0x5678;
        bus.WriteWord(cpu.PC, Sh2Asm.Clrmac());

        int cycles = cpu.Step();

        Assert.Equal(0u, cpu.MACH);
        Assert.Equal(0u, cpu.MACL);
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_Sleep_RewindsPcSoTheSameOpcodeReexecutes()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        uint start = cpu.PC;
        bus.WriteWord(start, Sh2Asm.Sleep());

        int cycles = cpu.Step();

        Assert.Equal(start, cpu.PC); // fetch advanced PC by 2, SLEEP rewinds it back
        Assert.Equal(3, cycles);
    }

    // ---- LDC/STC/LDS/STS register-move family ----

    [Fact]
    public void Step_StcSr_CopiesSrToRn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagT = true;
        cpu.InterruptMask = 5;
        bus.WriteWord(cpu.PC, Sh2Asm.StcSr(n: 3));

        cpu.Step();

        Assert.Equal(cpu.SR, cpu.R[3]);
    }

    [Fact]
    public void Step_StcGbr_CopiesGbrToRn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.GBR = 0x1234_5678u;
        bus.WriteWord(cpu.PC, Sh2Asm.StcGbr(n: 2));

        cpu.Step();

        Assert.Equal(0x1234_5678u, cpu.R[2]);
    }

    [Fact]
    public void Step_StcVbr_CopiesVbrToRn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.VBR = 0x0000_4000u;
        bus.WriteWord(cpu.PC, Sh2Asm.StcVbr(n: 2));

        cpu.Step();

        Assert.Equal(0x0000_4000u, cpu.R[2]);
    }

    [Fact]
    public void Step_StsMach_CopiesMachToRn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.MACH = 0xABCDu;
        bus.WriteWord(cpu.PC, Sh2Asm.StsMach(n: 2));

        cpu.Step();

        Assert.Equal(0xABCDu, cpu.R[2]);
    }

    [Fact]
    public void Step_StsMacl_CopiesMaclToRn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.MACL = 0x1357_9BDFu;
        bus.WriteWord(cpu.PC, Sh2Asm.StsMacl(n: 2));

        cpu.Step();

        Assert.Equal(0x1357_9BDFu, cpu.R[2]);
    }

    [Fact]
    public void Step_StsPr_CopiesPrToRn()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.PR = 0x0000_2000u;
        bus.WriteWord(cpu.PC, Sh2Asm.StsPr(n: 2));

        cpu.Step();

        Assert.Equal(0x0000_2000u, cpu.R[2]);
    }

    [Fact]
    public void Step_LdcSr_LoadsOnlyTheFlagsBitsFromRm()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0xFFFF_FFFFu; // every bit set, including ones FLAGS doesn't cover
        bus.WriteWord(cpu.PC, Sh2Asm.LdcSr(n: 1)); // register field, semantically Rm here

        cpu.Step();

        Assert.Equal(0x0000_03F3u, cpu.SR); // T|S|I3-I0|Q|M only
    }

    [Fact]
    public void Step_LdcGbr_CopiesRmToGbr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x9999_0000u;
        bus.WriteWord(cpu.PC, Sh2Asm.LdcGbr(n: 1));

        cpu.Step();

        Assert.Equal(0x9999_0000u, cpu.GBR);
    }

    [Fact]
    public void Step_LdcVbr_CopiesRmToVbr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_6000u;
        bus.WriteWord(cpu.PC, Sh2Asm.LdcVbr(n: 1));

        cpu.Step();

        Assert.Equal(0x0000_6000u, cpu.VBR);
    }

    [Fact]
    public void Step_LdsMach_CopiesRmToMach()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_1111u;
        bus.WriteWord(cpu.PC, Sh2Asm.LdsMach(n: 1));

        cpu.Step();

        Assert.Equal(0x0000_1111u, cpu.MACH);
    }

    [Fact]
    public void Step_LdsMacl_CopiesRmToMacl()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_2222u;
        bus.WriteWord(cpu.PC, Sh2Asm.LdsMacl(n: 1));

        cpu.Step();

        Assert.Equal(0x0000_2222u, cpu.MACL);
    }

    [Fact]
    public void Step_LdsPr_CopiesRmToPr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_3000u;
        bus.WriteWord(cpu.PC, Sh2Asm.LdsPr(n: 1));

        cpu.Step();

        Assert.Equal(0x0000_3000u, cpu.PR);
    }

    [Fact]
    public void Step_LdcMSr_LoadsMaskedFlagsFromMemoryAndPostIncrements()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_9000u;
        bus.WriteLong(cpu.R[1], 0xFFFF_FFFFu);
        bus.WriteWord(cpu.PC, Sh2Asm.LdcMSr(n: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_03F3u, cpu.SR);
        Assert.Equal(0x0000_9004u, cpu.R[1]);
        Assert.Equal(3, cycles);
    }

    [Fact]
    public void Step_LdcMGbr_LoadsFromMemoryAndPostIncrements()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_9000u;
        bus.WriteLong(cpu.R[1], 0x1234_5678u);
        bus.WriteWord(cpu.PC, Sh2Asm.LdcMGbr(n: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x1234_5678u, cpu.GBR);
        Assert.Equal(0x0000_9004u, cpu.R[1]);
        Assert.Equal(3, cycles);
    }

    [Fact]
    public void Step_LdcMVbr_LoadsFromMemoryAndPostIncrements()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_9000u;
        bus.WriteLong(cpu.R[1], 0x0000_5000u);
        bus.WriteWord(cpu.PC, Sh2Asm.LdcMVbr(n: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_5000u, cpu.VBR);
        Assert.Equal(0x0000_9004u, cpu.R[1]);
        Assert.Equal(3, cycles);
    }

    [Fact]
    public void Step_LdsMMach_LoadsFromMemoryAndPostIncrements()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_9000u;
        bus.WriteLong(cpu.R[1], 0x0000_AAAAu);
        bus.WriteWord(cpu.PC, Sh2Asm.LdsMMach(n: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_AAAAu, cpu.MACH);
        Assert.Equal(0x0000_9004u, cpu.R[1]);
        Assert.Equal(1, cycles); // unlike the LDC.L forms above, no extra cycles are charged
    }

    [Fact]
    public void Step_LdsMMacl_LoadsFromMemoryAndPostIncrements()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_9000u;
        bus.WriteLong(cpu.R[1], 0x0000_BBBBu);
        bus.WriteWord(cpu.PC, Sh2Asm.LdsMMacl(n: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_BBBBu, cpu.MACL);
        Assert.Equal(0x0000_9004u, cpu.R[1]);
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_LdsMPr_LoadsFromMemoryAndPostIncrements()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_9000u;
        bus.WriteLong(cpu.R[1], 0x0000_CCCCu);
        bus.WriteWord(cpu.PC, Sh2Asm.LdsMPr(n: 1));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_CCCCu, cpu.PR);
        Assert.Equal(0x0000_9004u, cpu.R[1]);
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_StcMSr_PreDecrementsAndStoresSrToMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.FlagT = true;
        cpu.R[2] = 0x0000_9010u;
        bus.WriteWord(cpu.PC, Sh2Asm.StcMSr(n: 2));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_900Cu, cpu.R[2]);
        Assert.Equal(cpu.SR, bus.ReadLong(cpu.R[2]));
        Assert.Equal(2, cycles);
    }

    [Fact]
    public void Step_StcMGbr_PreDecrementsAndStoresGbrToMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.GBR = 0x4444_5555u;
        cpu.R[2] = 0x0000_9010u;
        bus.WriteWord(cpu.PC, Sh2Asm.StcMGbr(n: 2));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_900Cu, cpu.R[2]);
        Assert.Equal(0x4444_5555u, bus.ReadLong(cpu.R[2]));
        Assert.Equal(2, cycles);
    }

    [Fact]
    public void Step_StcMVbr_PreDecrementsAndStoresVbrToMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.VBR = 0x0000_7000u;
        cpu.R[2] = 0x0000_9010u;
        bus.WriteWord(cpu.PC, Sh2Asm.StcMVbr(n: 2));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_900Cu, cpu.R[2]);
        Assert.Equal(0x0000_7000u, bus.ReadLong(cpu.R[2]));
        Assert.Equal(2, cycles);
    }

    [Fact]
    public void Step_StsMMach_PreDecrementsAndStoresMachToMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.MACH = 0x0000_DDDDu;
        cpu.R[2] = 0x0000_9010u;
        bus.WriteWord(cpu.PC, Sh2Asm.StsMMach(n: 2));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_900Cu, cpu.R[2]);
        Assert.Equal(0x0000_DDDDu, bus.ReadLong(cpu.R[2]));
        Assert.Equal(1, cycles); // unlike the STC.L forms above, no extra cycles are charged
    }

    [Fact]
    public void Step_StsMMacl_PreDecrementsAndStoresMaclToMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.MACL = 0x0000_EEEEu;
        cpu.R[2] = 0x0000_9010u;
        bus.WriteWord(cpu.PC, Sh2Asm.StsMMacl(n: 2));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_900Cu, cpu.R[2]);
        Assert.Equal(0x0000_EEEEu, bus.ReadLong(cpu.R[2]));
        Assert.Equal(1, cycles);
    }

    [Fact]
    public void Step_StsMPr_PreDecrementsAndStoresPrToMemory()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.PR = 0x0000_FFF0u;
        cpu.R[2] = 0x0000_9010u;
        bus.WriteWord(cpu.PC, Sh2Asm.StsMPr(n: 2));

        int cycles = cpu.Step();

        Assert.Equal(0x0000_900Cu, cpu.R[2]);
        Assert.Equal(0x0000_FFF0u, bus.ReadLong(cpu.R[2]));
        Assert.Equal(1, cycles);
    }

    // ---- SWAP / NEG / NEGC / EXT / TAS ----

    [Fact]
    public void Step_SwapB_SwapsTheLowTwoBytesOnly()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x1234_5678u;
        bus.WriteWord(cpu.PC, Sh2Asm.SwapB(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x1234_7856u, cpu.R[0]);
    }

    [Fact]
    public void Step_SwapW_SwapsTheTwo16BitHalves()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x1234_5678u;
        bus.WriteWord(cpu.PC, Sh2Asm.SwapW(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x5678_1234u, cpu.R[0]);
    }

    [Fact]
    public void Step_Neg_TwosComplementNegatesWithNoFlagsAffected()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 5;
        bus.WriteWord(cpu.PC, Sh2Asm.Neg(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0xFFFF_FFFBu, cpu.R[0]);
    }

    [Theory]
    [InlineData(5u, false, 0xFFFF_FFFBu, true)] // plain negate, no borrow-in
    [InlineData(0u, false, 0u, false)] // negating zero with no borrow-in
    [InlineData(0u, true, 0xFFFF_FFFFu, true)] // borrow-in alone forces a nonzero (all-ones) result
    public void Step_Negc_NegatesWithBorrowInAndOut(uint rm, bool carryIn, uint expectedRn, bool expectedT)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = rm;
        cpu.FlagT = carryIn;
        bus.WriteWord(cpu.PC, Sh2Asm.Negc(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(expectedRn, cpu.R[0]);
        Assert.Equal(expectedT, cpu.FlagT);
    }

    [Fact]
    public void Step_ExtuB_ZeroExtendsTheLowByte()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0xFFFF_FF81u;
        bus.WriteWord(cpu.PC, Sh2Asm.ExtuB(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x0000_0081u, cpu.R[0]);
    }

    [Fact]
    public void Step_ExtuW_ZeroExtendsTheLowWord()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0xFFFF_8001u;
        bus.WriteWord(cpu.PC, Sh2Asm.ExtuW(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0x0000_8001u, cpu.R[0]);
    }

    [Fact]
    public void Step_ExtsB_SignExtendsTheLowByte()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_0081u; // 0x81 as a signed byte is negative
        bus.WriteWord(cpu.PC, Sh2Asm.ExtsB(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0xFFFF_FF81u, cpu.R[0]);
    }

    [Fact]
    public void Step_ExtsW_SignExtendsTheLowWord()
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[1] = 0x0000_8001u; // 0x8001 as a signed word is negative
        bus.WriteWord(cpu.PC, Sh2Asm.ExtsW(m: 1, n: 0));

        cpu.Step();

        Assert.Equal(0xFFFF_8001u, cpu.R[0]);
    }

    [Theory]
    [InlineData((byte)0x00, true, (byte)0x80)] // zero byte -> T set, bit 7 forced on
    [InlineData((byte)0x05, false, (byte)0x85)] // nonzero byte -> T clear, bit 7 forced on
    public void Step_Tas_TestsThenSetsBit7OfTheByte(byte before, bool expectedT, byte expectedByte)
    {
        var (cpu, bus) = CreateCpu();
        cpu.Reset();
        cpu.R[0] = 0x0000_9000u;
        bus.WriteByte(cpu.R[0], before);
        bus.WriteWord(cpu.PC, Sh2Asm.Tas(n: 0));

        int cycles = cpu.Step();

        Assert.Equal(expectedT, cpu.FlagT);
        Assert.Equal(expectedByte, bus.ReadByte(cpu.R[0]));
        Assert.Equal(4, cycles);
    }
}
