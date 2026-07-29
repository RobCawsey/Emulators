using GenesisSharp.Cpu68000;

namespace GenesisSharp.Tests;

public class M68000PrivilegedTests
{
    private static (M68000 Cpu, FlatMemoryBus Bus) CreateCpu()
    {
        var bus = new FlatMemoryBus();
        return (new M68000(bus), bus);
    }

    [Fact]
    public void Stop_InSupervisorMode_LoadsSrAndHalts()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000; // supervisor, no flags
        bus.WriteWord(0x1000, Asm.Stop);
        bus.WriteWord(0x1002, 0x2704);

        int cycles = cpu.Step();

        Assert.Equal(0x2704, cpu.SR);
        Assert.True(cpu.Stopped);
        Assert.Equal(4, cycles);
        Assert.Equal(0x1004u, cpu.PC);
    }

    [Fact]
    public void Stop_WhileStopped_DoesNotFetchOrAdvancePc()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        bus.WriteWord(0x1000, Asm.Stop);
        bus.WriteWord(0x1002, 0x2000);
        cpu.Step(); // now stopped, PC = 0x1004

        int cycles = cpu.Step();

        Assert.Equal(0x1004u, cpu.PC);
        Assert.Equal(4, cycles);
        Assert.True(cpu.Stopped);

        cpu.Resume();
        Assert.False(cpu.Stopped);
    }

    [Fact]
    public void Stop_OutsideSupervisorMode_TrapsInsteadOfHalting()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000; // not supervisor
        cpu.A[7] = 0x2000;
        bus.WriteLong(0x20, 0x9000); // vector 8: privilege violation
        bus.WriteWord(0x1000, Asm.Stop);

        int cycles = cpu.Step();

        Assert.False(cpu.Stopped);
        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(0x1002u, bus.ReadLong(0x1FFC)); // return address — the STOP operand was never fetched
        Assert.Equal(34, cycles);
    }

    [Fact]
    public void Reset_InSupervisorMode_RaisesEventWithoutTouchingCpuState()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.D[0] = 0x1234;
        bool raised = false;
        cpu.ExternalDevicesReset += () => raised = true;
        bus.WriteWord(0x1000, Asm.Reset);

        int cycles = cpu.Step();

        Assert.True(raised);
        Assert.Equal(0x1002u, cpu.PC);
        Assert.Equal(0x1234u, cpu.D[0]); // RESET doesn't touch the 68000's own state
        Assert.Equal(132, cycles);
    }

    [Fact]
    public void Reset_OutsideSupervisorMode_TrapsWithoutRaisingTheEvent()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000;
        cpu.A[7] = 0x2000;
        bool raised = false;
        cpu.ExternalDevicesReset += () => raised = true;
        bus.WriteLong(0x20, 0x9000);
        bus.WriteWord(0x1000, Asm.Reset);

        cpu.Step();

        Assert.False(raised);
        Assert.Equal(0x9000u, cpu.PC);
    }

    [Fact]
    public void Trapv_WhenOverflowSet_TrapsViaVectorSeven()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.A[7] = 0x2000;
        cpu.FlagOverflow = true;
        bus.WriteLong(0x1C, 0x9000); // vector 7: overflow
        bus.WriteWord(0x1000, Asm.Trapv);

        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.Equal(34, cycles);
    }

    [Fact]
    public void Trapv_WhenOverflowClear_FallsThrough()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.FlagOverflow = false;
        bus.WriteWord(0x1000, Asm.Trapv);

        int cycles = cpu.Step();

        Assert.Equal(0x1002u, cpu.PC);
        Assert.Equal(4, cycles);
    }

    [Fact]
    public void MoveToCcr_ReplacesOnlyTheLowByteOfSr_AndIsNotPrivileged()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000; // not supervisor — must still work
        cpu.D[0] = 0x00F1;
        bus.WriteWord(0x1000, Asm.MoveToCcr(eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0x00F1, cpu.SR);
    }

    [Fact]
    public void MoveFromSr_CopiesFullSrIntoLowWordOfDestination_AndIsNotPrivileged()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000; // not supervisor — must still work on genuine MC68000
        cpu.SR = 0x2704;
        cpu.D[1] = 0xFFFF_0000;
        bus.WriteWord(0x1000, Asm.MoveFromSr(eaMode: 0, eaReg: 1));

        cpu.Step();

        Assert.Equal(0xFFFF_2704u, cpu.D[1]);
    }

    [Fact]
    public void MoveToSr_InSupervisorMode_ReplacesWholeSr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.D[0] = 0x0704; // no supervisor bit
        bus.WriteWord(0x1000, Asm.MoveToSr(eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0x0704, cpu.SR);
        Assert.False(cpu.Supervisor); // MOVE to SR can drop out of supervisor mode
    }

    [Fact]
    public void MoveToSr_OutsideSupervisorMode_TrapsAndLeavesSrUnwritten()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000;
        cpu.A[7] = 0x2000;
        cpu.D[0] = 0xDEAD;
        bus.WriteLong(0x20, 0x9000);
        bus.WriteWord(0x1000, Asm.MoveToSr(eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor); // set by exception entry, not by the (never-executed) MOVE
    }

    [Fact]
    public void AndiToCcr_MasksOnlyTheLowByte_AndIsNotPrivileged()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x27FF; // not-supervisor irrelevant here, but flags all set
        bus.WriteWord(0x1000, Asm.Andi(Size.Byte, eaMode: 7, eaReg: 4));
        bus.WriteWord(0x1002, 0x00F0);

        cpu.Step();

        Assert.Equal(0x27F0, cpu.SR);
    }

    [Fact]
    public void OriToSr_InSupervisorMode_OrsIntoTheWholeRegister()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        bus.WriteWord(0x1000, Asm.Ori(Size.Word, eaMode: 7, eaReg: 4));
        bus.WriteWord(0x1002, 0x0700);

        cpu.Step();

        Assert.Equal(0x2700, cpu.SR);
    }

    [Fact]
    public void OriToSr_OutsideSupervisorMode_Traps()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000;
        cpu.A[7] = 0x2000;
        bus.WriteLong(0x20, 0x9000);
        bus.WriteWord(0x1000, Asm.Ori(Size.Word, eaMode: 7, eaReg: 4));

        cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
    }

    [Fact]
    public void MoveUsp_ToUsp_ThenBack_RoundTripsThroughTheHiddenRegister()
    {
        // Found via real-ROM testing: this instruction wasn't implemented at all, and a
        // complete homebrew game (Crazy Driver) uses it during boot.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.A[3] = 0x00FF_1234;
        bus.WriteWord(0x1000, Asm.MoveUsp(toUsp: true, register: 3));
        bus.WriteWord(0x1002, Asm.MoveUsp(toUsp: false, register: 5));

        int firstCycles = cpu.Step();
        cpu.A[5] = 0; // prove the second instruction is what populates it, not a stale value
        cpu.Step();

        Assert.Equal(4, firstCycles);
        Assert.Equal(0x00FF_1234u, cpu.A[5]);
    }

    [Fact]
    public void MoveUsp_OutsideSupervisorMode_Traps()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x0000;
        cpu.A[7] = 0x2000;
        bus.WriteLong(0x20, 0x9000);
        bus.WriteWord(0x1000, Asm.MoveUsp(toUsp: true, register: 0));

        cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
    }

    [Fact]
    public void ReservedImmediateSize_TrapsViaVectorFour_WithoutFetchingAnImmediate()
    {
        // ADDI's size field only encodes byte/word/long (0/1/2) — size=3 is architecturally
        // reserved. Real hardware traps before ever reading the (nonexistent) immediate operand.
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.A[7] = 0x2000;
        bus.WriteLong(0x10, 0x9000); // vector 4: illegal instruction
        bus.WriteWord(0x1000, Asm.AddImmediateRawSize(sizeBits: 3, eaMode: 0, eaReg: 0));
        bus.WriteLong(0x1002, 0xDEAD_BEEF); // would-be immediate — must be left untouched/unread

        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(34, cycles);
        Assert.Equal(0x1002u, bus.ReadLong(0x1FFC)); // return address — right past the opcode word only
    }

    [Fact]
    public void ReservedSubNibble_TrapsViaVectorFour()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.SR = 0x2000;
        cpu.A[7] = 0x2000;
        bus.WriteLong(0x10, 0x9000); // vector 4: illegal instruction
        bus.WriteWord(0x1000, Asm.ReservedSubNibbleE());

        int cycles = cpu.Step();

        Assert.Equal(0x9000u, cpu.PC);
        Assert.True(cpu.Supervisor);
        Assert.Equal(34, cycles);
        Assert.Equal(0x1002u, bus.ReadLong(0x1FFC));
    }

    [Fact]
    public void Neg_StillDecodesCorrectly_NotMistakenForMoveToCcr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 5;
        bus.WriteWord(0x1000, Asm.Neg(Size.Word, eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0xFFFBu, cpu.D[0] & 0xFFFF); // -5
    }

    [Fact]
    public void Not_StillDecodesCorrectly_NotMistakenForMoveToSr()
    {
        var (cpu, bus) = CreateCpu();
        cpu.PC = 0x1000;
        cpu.D[0] = 0x00FF;
        bus.WriteWord(0x1000, Asm.Not(Size.Word, eaMode: 0, eaReg: 0));

        cpu.Step();

        Assert.Equal(0xFF00u, cpu.D[0] & 0xFFFF);
    }
}
