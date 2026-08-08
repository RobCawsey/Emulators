using GenesisSharp.Core;

namespace GenesisSharp.Tests;

/// <summary>Phase 4 of the in-progress 32X extension (see ARCHITECTURE.md §4a.3): the PWM
/// register interface, FIFO, and sample-generation logic. Tests drive <see cref="Sega32X"/>
/// directly through its existing public byte-level register methods (the same ones
/// <see cref="GenesisConsole"/>'s 68000-side bus already routes through, confirmed in Phase 2) —
/// no new public surface was needed for this phase. Mirrors Sega32XVdpTests.cs's style.</summary>
public class Sega32XPwmTests
{
    private const double OneAudioSample = 1.0 / 44100.0;

    /// <summary>A 32X reset with the power-on VRES then acknowledged on both cores, which is the
    /// state these tests want. The acknowledgment is not incidental: a whole-system reset leaves
    /// VRES asserted at level 14 (see <c>Sega32X.RaiseVResInterrupt</c>), and since the five
    /// sources are level-triggered it stays asserted — outranking every lower-priority source these
    /// tests are actually about — until something clears it. Writing adapter offset 0x14 is exactly
    /// what a real VRES handler does, and the only thing that clears it.</summary>
    private static Sega32X CreateSega32X()
    {
        var sega32X = new Sega32X(Cartridge.LoadFromBin(new byte[0x10000]));
        sega32X.Reset();
        sega32X.MasterSh2Bus.WriteByte(0x4014, 0); // VRES ack, master
        sega32X.SlaveSh2Bus.WriteByte(0x4014, 0); // VRES ack, slave
        return sega32X;
    }

    // SH-2 on-chip DMAC channel 1, as real addresses ($FFFFFE00 + the peripheral-block offsets in
    // Sega32X.Bus.cs). Channel 1 specifically, because that is the one PWM's RTP drives.
    private const uint Dmac1SarAddress = 0xFFFF_FF90;
    private const uint Dmac1DarAddress = 0xFFFF_FF94;
    private const uint Dmac1TcrAddress = 0xFFFF_FF98;
    private const uint Dmac1ChcrAddress = 0xFFFF_FF9C;
    private const uint DmaorAddress = 0xFFFF_FFB0;

    /// <summary>Arms master channel 1 as a real PWM feed would: word-sized, source incrementing
    /// through SDRAM, destination held on the PWM left-FIFO register, AR clear (so it waits for an
    /// external request rather than running immediately), DE set.</summary>
    private static void ArmMasterDreq1Channel(Sega32X sega32X, uint transferCount)
    {
        sega32X.MasterSh2Bus.WriteLong(Dmac1SarAddress, 0x0600_0000); // SDRAM
        sega32X.MasterSh2Bus.WriteLong(Dmac1DarAddress, 0x2000_4034); // PWM left/mono FIFO
        sega32X.MasterSh2Bus.WriteLong(Dmac1TcrAddress, transferCount);
        sega32X.MasterSh2Bus.WriteLong(Dmac1ChcrAddress, 0x1401); // SAR+ (bit12), word (bits11:10=01), DE
        sega32X.MasterSh2Bus.WriteLong(DmaorAddress, 0x0001); // DME
    }

    /// <summary>Drives exactly one PWM interrupt. The cycle register is deliberately large enough
    /// that a single audio sample's worth of elapsed PWM clock covers exactly one period — with a
    /// small one, a single call would consume hundreds of periods and fire hundreds of interrupts,
    /// which would make "one request moved one unit" impossible to observe.</summary>
    private static void RaiseExactlyOnePwmInterrupt(Sega32X sega32X)
    {
        SetCycleRegister(sega32X, 401); // period 400 vs ~522 cycles elapsed per audio sample
        sega32X.MasterSh2Bus.WriteByte(0x4030, 0x01); // IRQ-timer nibble = 1 -> every period
        sega32X.GeneratePwmSample(OneAudioSample);
    }

    private static void SetCycleRegister(Sega32X sega32X, ushort value)
    {
        sega32X.WriteControlByteFrom68k(0x32, (byte)(value >> 8));
        sega32X.WriteControlByteFrom68k(0x33, (byte)value);
    }

    private static void SetXmd(Sega32X sega32X, byte xmd) => sega32X.WriteControlByteFrom68k(0x31, xmd);

    private static void WriteLeftFifo(Sega32X sega32X, ushort d)
    {
        sega32X.WriteControlByteFrom68k(0x34, (byte)(d >> 8));
        sega32X.WriteControlByteFrom68k(0x35, (byte)d); // odd address triggers the push
    }

    private static void WriteRightFifo(Sega32X sega32X, ushort d)
    {
        sega32X.WriteControlByteFrom68k(0x36, (byte)(d >> 8));
        sega32X.WriteControlByteFrom68k(0x37, (byte)d);
    }

    [Fact]
    public void GeneratePwmSample_PowerOnDefault_IsAlwaysSilent()
    {
        var sega32X = CreateSega32X(); // xMd = 0 -- confirmed-invalid, the power-on default

        var (left, right) = sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal((short)0, left);
        Assert.Equal((short)0, right);
    }

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x06)]
    [InlineData((byte)0x09)]
    [InlineData((byte)0x0F)]
    public void GeneratePwmSample_ConfirmedInvalidRouting_IsAlwaysSilent(byte xmd)
    {
        var sega32X = CreateSega32X();
        SetXmd(sega32X, xmd);
        WriteLeftFifo(sega32X, 0xFFF); // even with data queued, invalid routing stays silent
        WriteRightFifo(sega32X, 0xFFF);

        var (left, right) = sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal((short)0, left);
        Assert.Equal((short)0, right);
    }

    [Fact]
    public void GeneratePwmSample_ConvertSample_ScalesLinearlyAroundMidScaleSilence()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x0100); // cycles = (0x100-1)&0xFFF = 255, mult = 0x1000000/256 = 0x10000
        SetXmd(sega32X, 0x05); // normal stereo
        WriteLeftFifo(sega32X, 129); // stored = (129-1)&0xFFF = 128 -> exactly half of (cycles+1) -> mid-scale
        WriteRightFifo(sega32X, 129);

        var (left, right) = sega32X.GeneratePwmSample(OneAudioSample); // one real audio-sample tick is enough

        Assert.Equal((short)0, left); // (128*0x10000>>8) - 0x8000 == 0
        Assert.Equal((short)0, right);
    }

    /// <summary>32X interrupt-routing plan Phase 3: PWM's real "FIFO needs feeding" signal.
    /// Confirmed against PicoDrive's own <c>consume_fifo_do</c> (<c>pwm.c:112-114</c>) -- fires
    /// once every (IRQ-timer-nibble + 1) samples actually consumed, regardless of whether the
    /// FIFO underruns. The IRQ-timer nibble (adapter offset 0x30, the control register's high
    /// byte) is SH-2-exclusive, confirmed via <see cref="Sega32X.MasterSh2Bus"/>'s own SH-2-side
    /// write path rather than <see cref="Sega32X.WriteControlByteFrom68k"/> (which silently drops
    /// a 68000 write there).</summary>
    [Fact]
    public void GeneratePwmSample_IrqTimerElapses_RaisesPwmInterruptOnUnmaskedCoreOnly()
    {
        var sega32X = CreateSega32X();
        sega32X.MasterSh2Bus.WriteByte(0x4001, 0x01); // PwmMaskBit (bit 0) on master only
        sega32X.MasterSh2Bus.WriteByte(0x4030, 0x01); // IRQ-timer nibble = 1 -> reload fires every sample
        SetCycleRegister(sega32X, 1); // smallest real period, easily covered by one audio-sample tick
        SetXmd(sega32X, 0x05);
        WriteLeftFifo(sega32X, 0x800);
        WriteRightFifo(sega32X, 0x800);

        sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal(6, sega32X.MasterSh2.InterruptRequestLevel); // PwmLevel
        Assert.Equal(0, sega32X.SlaveSh2.InterruptRequestLevel); // never unmasked -- never raised
    }

    [Fact]
    public void GeneratePwmSample_PwmMaskedOffOnBothCores_NeverRaisesInterrupt()
    {
        var sega32X = CreateSega32X(); // Sh2IrqMask stays all-zero, the power-on default
        SetCycleRegister(sega32X, 1);
        SetXmd(sega32X, 0x05);
        WriteLeftFifo(sega32X, 0x800);
        WriteRightFifo(sega32X, 0x800);

        sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal(0, sega32X.MasterSh2.InterruptRequestLevel);
        Assert.Equal(0, sega32X.SlaveSh2.InterruptRequestLevel);
    }

    [Fact]
    public void GeneratePwmSample_LowFifoValue_ProducesNegativeSample()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x0100);
        SetXmd(sega32X, 0x05);
        WriteLeftFifo(sega32X, 1); // stored = 0 -> the minimum writable duty value

        var (left, _) = sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal(short.MinValue, left);
    }

    [Fact]
    public void GeneratePwmSample_NormalStereo_RoutesLeftAndRightToTheirOwnChannels()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x0100);
        SetXmd(sega32X, 0x05); // normal stereo
        WriteLeftFifo(sega32X, 1); // -> minimum
        WriteRightFifo(sega32X, 256); // stored = 255 = cycles -> maximum

        var (left, right) = sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal(short.MinValue, left);
        Assert.True(right > 0);
    }

    [Fact]
    public void GeneratePwmSample_SwappedStereo_SwapsLeftAndRight()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x0100);
        SetXmd(sega32X, 0x0A); // swapped stereo
        WriteLeftFifo(sega32X, 1); // minimum -- should end up on the RIGHT output now
        WriteRightFifo(sega32X, 256); // maximum -- should end up on the LEFT output now

        var (left, right) = sega32X.GeneratePwmSample(OneAudioSample);

        Assert.True(left > 0);
        Assert.Equal(short.MinValue, right);
    }

    [Fact]
    public void GeneratePwmSample_MonoRouting_DuplicatesTheLeftMonoChannelToBoth()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x0100);
        SetXmd(sega32X, 0x01); // any non-confirmed value other than 0x05/0x0A -- treated as mono
        WriteLeftFifo(sega32X, 1);

        var (left, right) = sega32X.GeneratePwmSample(OneAudioSample);

        Assert.Equal(left, right);
        Assert.Equal(short.MinValue, left);
    }

    [Fact]
    public void GeneratePwmSample_FifoUnderrun_HoldsThePreviousSampleInsteadOfGoingSilent()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x0100);
        SetXmd(sega32X, 0x05);
        WriteLeftFifo(sega32X, 256); // maximum -- queue exactly one entry

        var (firstLeft, _) = sega32X.GeneratePwmSample(OneAudioSample); // consumes the one queued entry
        var (secondLeft, _) = sega32X.GeneratePwmSample(OneAudioSample); // FIFO now empty -- should hold

        Assert.True(firstLeft > 0);
        Assert.Equal(firstLeft, secondLeft);
    }

    [Fact]
    public void FifoStatus_EmptyBitSetWhenNothingQueued()
    {
        var sega32X = CreateSega32X();

        byte status = sega32X.ReadControlByteFor68k(0x34); // high byte of the Left FIFO register

        Assert.NotEqual(0, status & 0x40); // P32XP_EMPTY
        Assert.Equal(0, status & 0x80); // P32XP_FULL
    }

    [Fact]
    public void FifoStatus_FullBitSetOnceThreeEntriesAreQueued()
    {
        var sega32X = CreateSega32X();
        WriteLeftFifo(sega32X, 1);
        WriteLeftFifo(sega32X, 2);
        WriteLeftFifo(sega32X, 3);

        byte status = sega32X.ReadControlByteFor68k(0x34);

        Assert.NotEqual(0, status & 0x80); // P32XP_FULL
        Assert.Equal(0, status & 0x40);
    }

    [Fact]
    public void Fifo_LowByteReadNeverExposesStatusBits()
    {
        var sega32X = CreateSega32X();

        Assert.Equal(0, sega32X.ReadControlByteFor68k(0x35)); // low byte -- status only lives in the high byte
    }

    [Fact]
    public void Fifo_Overflow_DropsTheOldestQueuedEntryRatherThanBlocking()
    {
        var sega32X = CreateSega32X();
        SetCycleRegister(sega32X, 0x1000); // cycles = (0x1000-1)&0xFFF = 4095, mult = 0x1000000/4096 = 4096
        SetXmd(sega32X, 0x05);
        WriteLeftFifo(sega32X, 100); // oldest -- must be dropped
        WriteLeftFifo(sega32X, 200);
        WriteLeftFifo(sega32X, 300); // FIFO now full (depth 3)
        WriteLeftFifo(sega32X, 400); // overflow -- should evict the 100 entry, not block

        // Draining order should now be 200, 300, 400 (100 was dropped). Cycle register is huge
        // enough that a single audio sample's worth of debt won't consume anything on its own,
        // so drive the clock forward explicitly by calling with a duration chosen to cross
        // exactly one period each time.
        double onePeriod = 4096.0 / (7_670_454.0 * 3.0) * 1.01;
        var (first, _) = sega32X.GeneratePwmSample(onePeriod);
        var (second, _) = sega32X.GeneratePwmSample(onePeriod);
        var (third, _) = sega32X.GeneratePwmSample(onePeriod);

        // Exact expected values (not just monotonicity -- a "drop the newest write instead"
        // overflow policy would also produce a monotonically increasing sequence, just a
        // different one, so only exact values genuinely distinguish the two): with mult=4096 and
        // an 8-bit shift, ConvertSample reduces to `stored*16 - 32768` here. Stored values for
        // 200/300/400 are (d-1)&0xFFF = 199/299/399.
        Assert.Equal((short)(199 * 16 - 32768), first);
        Assert.Equal((short)(299 * 16 - 32768), second);
        Assert.Equal((short)(399 * 16 - 32768), third);
    }

    [Fact]
    public void ControlRegister_68kCanOnlyWriteTheLowFourBits()
    {
        var sega32X = CreateSega32X();

        sega32X.WriteControlByteFrom68k(0x30, 0xFF); // high byte: IRQ-timer nibble -- 68k-exclusive, must be ignored
        sega32X.WriteControlByteFrom68k(0x31, 0xFF); // low byte: only xMd (low nibble) should land, not RTP (bit7)

        Assert.Equal(0, sega32X.ReadControlByteFor68k(0x30));
        Assert.Equal(0x0F, sega32X.ReadControlByteFor68k(0x31));
    }

    /// <summary>RTP's DMA auto-feed: each PWM interrupt asserts the chip's DREQ line, and the DMAC
    /// answers with exactly <em>one</em> transfer unit — not the whole block. Confirmed against
    /// PicoDrive's <c>dreq1_do</c> (sh2soc.c), which calls <c>dmac_transfer_one</c> and completes
    /// only once TCR reaches zero. Draining the whole buffer per request would overrun PWM's own
    /// 3-entry FIFO immediately, so the "one unit" part is the behaviour, not an optimisation.</summary>
    [Fact]
    public void RtpSet_OnePwmInterrupt_MovesExactlyOneDmaUnit()
    {
        var sega32X = CreateSega32X();
        ArmMasterDreq1Channel(sega32X, transferCount: 4);
        sega32X.MasterSh2Bus.WriteByte(0x4031, 0x05 | 0x80); // xMd = normal stereo, RTP set

        RaiseExactlyOnePwmInterrupt(sega32X);

        Assert.Equal(3u, sega32X.MasterSh2Bus.ReadLong(Dmac1TcrAddress)); // 4 -> 3, one unit
        Assert.Equal(0x0600_0002u, sega32X.MasterSh2Bus.ReadLong(Dmac1SarAddress)); // advanced one word
        Assert.Equal(0x2000_4034u, sega32X.MasterSh2Bus.ReadLong(Dmac1DarAddress)); // FIFO address held
    }

    /// <summary>Without RTP the same PWM interrupt must not move any data — the interrupt and the
    /// DMA request are separate consequences of the same event, and only the latter is gated by
    /// this bit.</summary>
    [Fact]
    public void RtpClear_PwmInterrupt_DoesNotTriggerDma()
    {
        var sega32X = CreateSega32X();
        ArmMasterDreq1Channel(sega32X, transferCount: 4);
        sega32X.MasterSh2Bus.WriteByte(0x4031, 0x05); // xMd set, RTP clear

        RaiseExactlyOnePwmInterrupt(sega32X);

        Assert.Equal(4u, sega32X.MasterSh2Bus.ReadLong(Dmac1TcrAddress)); // untouched
    }

    /// <summary>An external-request channel must not run just because it was armed: that is what
    /// separates it from an auto-request (AR) transfer, which fires on the arming write itself.
    /// Guards against the DREQ1 work regressing into "start immediately".</summary>
    [Fact]
    public void ExternalRequestChannel_ArmedButNeverRequested_TransfersNothing()
    {
        var sega32X = CreateSega32X();

        ArmMasterDreq1Channel(sega32X, transferCount: 4); // AR clear -- waits for a DREQ

        Assert.Equal(4u, sega32X.MasterSh2Bus.ReadLong(Dmac1TcrAddress));
        Assert.Equal(0u, sega32X.MasterSh2Bus.ReadLong(Dmac1ChcrAddress) & 0x2); // TE still clear
    }

    /// <summary>The last unit of a DREQ-driven transfer sets CHCR's TE bit, which is how software
    /// polling the register (rather than taking the completion interrupt) sees it finish.</summary>
    [Fact]
    public void Dreq1_OnTheFinalUnit_SetsTransferEnd()
    {
        var sega32X = CreateSega32X();
        ArmMasterDreq1Channel(sega32X, transferCount: 1); // one unit left
        sega32X.MasterSh2Bus.WriteByte(0x4031, 0x05 | 0x80);

        RaiseExactlyOnePwmInterrupt(sega32X);

        Assert.Equal(0u, sega32X.MasterSh2Bus.ReadLong(Dmac1TcrAddress));
        Assert.NotEqual(0u, sega32X.MasterSh2Bus.ReadLong(Dmac1ChcrAddress) & 0x2); // TE set
    }
}
