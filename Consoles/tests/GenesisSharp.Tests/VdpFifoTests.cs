namespace GenesisSharp.Tests;

using GenesisSharp.Core;

public class VdpFifoTests
{
    private static ushort RegisterWriteWord(int register, byte data) => (ushort)(0x8000 | (register << 8) | data);

    /// <summary>Display enable (register 1 bit 6) plus a scanline inside the active area is
    /// what selects the slower "active display" external-slot rate — a fresh <see cref="Vdp"/>
    /// defaults to display disabled, which this emulator treats the same as blanking for slot
    /// purposes, so tests that want to exercise real stalling need this explicitly.</summary>
    private static Vdp CreateActiveDisplayVdp()
    {
        var vdp = new Vdp();
        vdp.WriteControlPort(RegisterWriteWord(1, 0x40));
        return vdp;
    }

    private static void WriteVramWord(Vdp vdp, ushort value)
    {
        // Code=1 (VRAM write), address 0 -- the exact address doesn't matter for these tests.
        vdp.WriteControlPort((ushort)(1 << 14));
        vdp.WriteControlPort(0x0000);
        vdp.WriteDataPort(value);
    }

    [Fact]
    public void WriteDataPort_UpToFourPendingWrites_DoesNotStallCpu()
    {
        // Found via real-ROM testing (Omega Blast): this emulator used to treat every VDP
        // data-port write as instant, with no notion of the VDP's real 4-entry command FIFO --
        // confirmed missing via BlastEm's vdp.c (FIFO_SIZE=4), one of the few emulators
        // accurate enough to pass Nemesis's VDP FIFO test ROM. Four writes with zero elapsed
        // time between them should still all fit without stalling.
        var vdp = CreateActiveDisplayVdp();

        for (int i = 0; i < 4; i++)
        {
            WriteVramWord(vdp, (ushort)i);
        }

        Assert.Equal(0, vdp.ConsumeStallCycles());
    }

    [Fact]
    public void WriteDataPort_FifthPendingWriteWithNoElapsedTime_StallsCpu()
    {
        var vdp = CreateActiveDisplayVdp();

        for (int i = 0; i < 4; i++)
        {
            WriteVramWord(vdp, (ushort)i);
        }

        WriteVramWord(vdp, 0x1234); // the 5th, with no AdvanceExternalSlotClock call in between

        Assert.True(vdp.ConsumeStallCycles() > 0);
    }

    [Fact]
    public void AdvanceExternalSlotClock_DrainsFifoOverTime_SoLaterWritesDoNotStall()
    {
        var vdp = CreateActiveDisplayVdp();

        for (int i = 0; i < 4; i++)
        {
            WriteVramWord(vdp, (ushort)i);
        }

        // Enough elapsed 68000 cycles for every pending entry to fully drain even at the slow
        // active-display external-slot rate (~27 cycles/slot here, 2 slots/entry, 4 entries).
        vdp.AdvanceExternalSlotClock(1000);

        WriteVramWord(vdp, 0x1234);

        Assert.Equal(0, vdp.ConsumeStallCycles());
    }

    [Fact]
    public void ConsumeStallCycles_ResetsAfterReading()
    {
        var vdp = CreateActiveDisplayVdp();
        for (int i = 0; i < 5; i++)
        {
            WriteVramWord(vdp, (ushort)i);
        }

        Assert.True(vdp.ConsumeStallCycles() > 0);
        Assert.Equal(0, vdp.ConsumeStallCycles());
    }

    [Fact]
    public void FifoOverflowStall_IsCheaperDuringBlankingThanActiveDisplay()
    {
        // Real hardware's VDP claims most of a scanline's access slots for its own rendering
        // fetches while actively displaying, leaving the CPU/DMA only a handful -- during
        // VBlank (or with display off) nearly every slot is free instead, so the same FIFO
        // overflow should cost far fewer cycles.
        var active = CreateActiveDisplayVdp();
        for (int i = 0; i < 5; i++)
        {
            WriteVramWord(active, (ushort)i);
        }
        int activeStall = active.ConsumeStallCycles();

        var blanking = new Vdp(); // display disabled by default -- treated as blanking
        for (int i = 0; i < 5; i++)
        {
            WriteVramWord(blanking, (ushort)i);
        }
        int blankingStall = blanking.ConsumeStallCycles();

        Assert.True(activeStall > 0);
        Assert.True(blankingStall > 0);
        Assert.True(blankingStall < activeStall);
    }

    [Fact]
    public void RunVramFill_ChargesStallProportionalToLength()
    {
        var vdp = CreateActiveDisplayVdp();
        vdp.WriteControlPort(RegisterWriteWord(1, 0x50)); // display enable + DMA enable
        vdp.WriteControlPort(RegisterWriteWord(15, 1)); // auto-increment = 1
        vdp.WriteControlPort(RegisterWriteWord(19, 8)); // DMA length = 8 bytes
        vdp.WriteControlPort(RegisterWriteWord(20, 0));
        vdp.WriteControlPort(RegisterWriteWord(23, 0x80)); // fill mode

        vdp.WriteControlPort((ushort)((0x21 & 0x3) << 14 | 0x2000));
        vdp.WriteControlPort((ushort)(((0x21 >> 2) & 0xF) << 4));
        vdp.WriteDataPort(0x00AB); // fill byte -- triggers the fill

        // 8 bytes * 1 slot/byte, at the active-display external-slot rate.
        Assert.True(vdp.ConsumeStallCycles() > 0);
    }

    [Fact]
    public void ReadStatusRegister_FifoBits_ReflectRealFifoState()
    {
        var vdp = CreateActiveDisplayVdp();

        Assert.Equal(0x0200, vdp.ReadStatusRegister() & 0x0300); // empty, not full

        for (int i = 0; i < 4; i++)
        {
            WriteVramWord(vdp, (ushort)i);
        }

        Assert.Equal(0x0100, vdp.ReadStatusRegister() & 0x0300); // full, not empty
    }
}
