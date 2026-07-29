using NesSharp.Core;
using NesSharp.Cpu;
using Xunit;

namespace NesSharp.Tests;

public class Apu2A03Tests
{
    [Fact]
    public void Envelope_ConstantVolume_AlwaysReturnsThatValue()
    {
        var envelope = new ApuEnvelope { ConstantVolume = true, VolumeOrPeriod = 9 };
        for (int i = 0; i < 20; i++)
        {
            envelope.Clock();
        }
        Assert.Equal(9, envelope.Output);
    }

    [Fact]
    public void Envelope_Decay_CountsDownEveryPeriodClocksThenHalts()
    {
        var envelope = new ApuEnvelope { ConstantVolume = false, VolumeOrPeriod = 0, LoopFlag = false, StartFlag = true };
        // period=0 means the divider reloads to 0 every clock, so decay drops one level per Clock().
        envelope.Clock(); // start flag: resets to 15
        Assert.Equal(15, envelope.Output);
        for (int i = 0; i < 15; i++)
        {
            envelope.Clock();
        }
        Assert.Equal(0, envelope.Output);
        envelope.Clock(); // no loop: stays at 0, doesn't wrap
        Assert.Equal(0, envelope.Output);
    }

    [Fact]
    public void Envelope_Decay_LoopsBackTo15WhenLoopFlagSet()
    {
        var envelope = new ApuEnvelope { ConstantVolume = false, VolumeOrPeriod = 0, LoopFlag = true, StartFlag = true };
        envelope.Clock();
        for (int i = 0; i < 15; i++)
        {
            envelope.Clock();
        }
        Assert.Equal(0, envelope.Output);
        envelope.Clock();
        Assert.Equal(15, envelope.Output);
    }

    [Fact]
    public void PulseChannel_LengthCounter_LoadsFromTableAndCountsDownToSilence()
    {
        var pulse = new ApuPulseChannel(isPulse1: true);
        pulse.SetEnabled(true);
        pulse.WriteControl(0x10); // loop=0 (halt disabled -> length actually decrements), constant volume, vol=0
        pulse.WriteTimerLow(0x00);
        pulse.WriteTimerHighAndLength(0x08); // length index = 1 -> table value 254

        Assert.Equal(254, pulse.LengthCounter);
        for (int i = 0; i < 254; i++)
        {
            pulse.ClockLengthAndSweep();
        }
        Assert.Equal(0, pulse.LengthCounter);
    }

    [Fact]
    public void PulseChannel_DisablingImmediatelyZeroesLengthCounter()
    {
        var pulse = new ApuPulseChannel(isPulse1: true);
        pulse.SetEnabled(true);
        pulse.WriteTimerHighAndLength(0x00); // length index 0 -> table value 10

        Assert.Equal(10, pulse.LengthCounter);
        pulse.SetEnabled(false);
        Assert.Equal(0, pulse.LengthCounter);
    }

    [Fact]
    public void PulseChannel_Sweep_RaisesPeriodWhenNotNegating()
    {
        var pulse = new ApuPulseChannel(isPulse1: true);
        pulse.SetEnabled(true);
        pulse.WriteTimerLow(0x64);
        pulse.WriteTimerHighAndLength(0x01); // period = 0x164 (356)
        int initialPeriod = GetTimerPeriod(pulse);

        pulse.WriteSweep(0x81); // enabled, sweep period=0, negate=0, shift=1
        pulse.ClockLengthAndSweep(); // divider defaults to 0 -> applies on this very first call

        Assert.True(GetTimerPeriod(pulse) > initialPeriod);
    }

    [Fact]
    public void PulseChannel_MutedWhenPeriodBelowEight()
    {
        var pulse = new ApuPulseChannel(isPulse1: true);
        pulse.SetEnabled(true);
        pulse.WriteControl(0x1F); // constant volume, volume 15
        pulse.WriteTimerLow(0x03); // period = 3, below the mute threshold of 8
        pulse.WriteTimerHighAndLength(0x08);

        Assert.Equal(0, pulse.Output);
    }

    private static int GetTimerPeriod(ApuPulseChannel pulse)
    {
        // No public getter for the raw period (nothing outside the channel needs it) — infer
        // it indirectly isn't practical here, so use reflection for this one white-box check.
        var field = typeof(ApuPulseChannel).GetField("_timerPeriod",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        return (ushort)field.GetValue(pulse)!;
    }

    [Fact]
    public void TriangleChannel_LinearCounterGatesOutput()
    {
        var triangle = new ApuTriangleChannel();
        triangle.SetEnabled(true);
        triangle.WriteTimerLow(0x00);
        triangle.WriteTimerHighAndLength(0x08); // length index 1 -> 254
        triangle.WriteControl(0x7F); // control=0 (not held), reload=127

        triangle.ClockLinearCounter(); // reload flag set by the $400B write -> linear counter becomes 127
        Assert.True(triangle.Output is >= 0 and <= 15); // gated on, sequencer running (starts at step 0 = 15)
        Assert.Equal(15, triangle.Output);
    }

    [Fact]
    public void TriangleChannel_ZeroLinearCounter_SilencesOutput()
    {
        var triangle = new ApuTriangleChannel();
        triangle.SetEnabled(true);
        triangle.WriteTimerHighAndLength(0x08);
        triangle.WriteControl(0x00); // reload value 0
        triangle.ClockLinearCounter(); // linear counter becomes 0

        Assert.Equal(0, triangle.Output);
    }

    [Fact]
    public void NoiseChannel_ConstantVolume_OutputIsZeroOrTheConfiguredVolume()
    {
        var noise = new ApuNoiseChannel();
        noise.SetEnabled(true);
        noise.WriteControl(0x1A); // constant volume, volume 10
        noise.WritePeriod(0x00); // shortest period, mode normal
        noise.WriteLength(0x08);

        var seen = new HashSet<byte>();
        for (int i = 0; i < 200; i++)
        {
            noise.ClockTimer();
            seen.Add(noise.Output);
        }

        Assert.True(seen.IsSubsetOf(new byte[] { 0, 10 }));
        Assert.Contains((byte)0, seen);
        Assert.Contains((byte)10, seen);
    }

    [Fact]
    public void NoiseChannel_NormalAndShortMode_ProduceDifferentSequences()
    {
        var normal = new ApuNoiseChannel();
        normal.SetEnabled(true);
        normal.WriteControl(0x1F);
        normal.WritePeriod(0x00);
        normal.WriteLength(0x08);

        var shortMode = new ApuNoiseChannel();
        shortMode.SetEnabled(true);
        shortMode.WriteControl(0x1F);
        shortMode.WritePeriod(0x80);
        shortMode.WriteLength(0x08);

        // Period 4 means only ~1 in 5 ClockTimer calls actually shifts the LFSR — enough
        // iterations are needed for the two tap positions to visibly diverge.
        var normalSeq = new List<byte>();
        var shortSeq = new List<byte>();
        for (int i = 0; i < 2000; i++)
        {
            normal.ClockTimer();
            shortMode.ClockTimer();
            normalSeq.Add(normal.Output);
            shortSeq.Add(shortMode.Output);
        }

        Assert.NotEqual(normalSeq, shortSeq);
    }

    [Fact]
    public void DmcChannel_PlaysAllOnesSample_RampsOutputUp()
    {
        var bus = new FlatRamBus();
        bus.Load(0xC000, 0xFF);
        var dmc = new ApuDmcChannel(bus);
        dmc.WriteSampleAddress(0x00); // -> $C000
        dmc.WriteSampleLength(0x00); // 1 byte
        dmc.SetEnabled(true);
        dmc.WriteControl(0x0F); // fastest rate, no loop, no IRQ

        byte before = dmc.Output;
        for (int i = 0; i < 8 * 60; i++) // 8 bits, generous cycle budget at the fastest rate
        {
            dmc.ClockTimer();
        }

        Assert.True(dmc.Output > before || dmc.Output == 127); // ramped up (clamped at 127)
        Assert.Equal(0, dmc.BytesRemaining);
    }

    [Fact]
    public void DmcChannel_SetsIrqFlag_WhenSampleFinishesWithoutLoop()
    {
        var bus = new FlatRamBus();
        bus.Load(0xC000, 0x00);
        var dmc = new ApuDmcChannel(bus);
        dmc.WriteSampleAddress(0x00);
        dmc.WriteSampleLength(0x00); // 1 byte
        dmc.WriteControl(0x8F); // IRQ enable, no loop, fastest rate
        dmc.SetEnabled(true);

        for (int i = 0; i < 8 * 60; i++)
        {
            dmc.ClockTimer();
        }

        Assert.True(dmc.IrqFlag);
    }

    [Fact]
    public void DmcChannel_FetchingASample_StallsTheAttachedCpu()
    {
        var bus = new FlatRamBus();
        bus.Load(0xC000, 0xAA);
        bus.SetResetVector(0x8000);
        var cpu = new Cpu6502(bus);
        cpu.Reset();
        cpu.RunUntilBoundary();

        var dmc = new ApuDmcChannel(bus);
        dmc.AttachCpu(cpu);
        dmc.WriteSampleAddress(0x00);
        dmc.WriteSampleLength(0x00);
        dmc.SetEnabled(true);

        Assert.False(cpu.IsMidInstruction);
        dmc.ClockTimer(); // triggers the fetch on the first call since the buffer starts empty
        Assert.True(cpu.IsMidInstruction); // 4 stall cycles now queued
    }

    [Fact]
    public void FrameSequencer_FourStepMode_RaisesIrqAtExpectedCycle()
    {
        var apu = new Apu2A03(new FlatRamBus());
        apu.WriteFrameCounter(0x00); // 4-step, IRQ enabled

        for (int i = 0; i < 29829; i++)
        {
            apu.Clock();
        }

        Assert.Equal(0x40, apu.ReadStatus() & 0x40);
    }

    [Fact]
    public void FrameSequencer_FiveStepMode_NeverRaisesIrq()
    {
        var apu = new Apu2A03(new FlatRamBus());
        apu.WriteFrameCounter(0x80); // 5-step

        for (int i = 0; i < 40000; i++)
        {
            apu.Clock();
        }

        Assert.Equal(0, apu.ReadStatus() & 0x40);
    }

    [Fact]
    public void FrameSequencer_IrqInhibitBit_SuppressesIrq()
    {
        var apu = new Apu2A03(new FlatRamBus());
        apu.WriteFrameCounter(0x40); // 4-step, IRQ inhibited

        for (int i = 0; i < 29829; i++)
        {
            apu.Clock();
        }

        Assert.Equal(0, apu.ReadStatus() & 0x40);
    }

    [Fact]
    public void ReadStatus_ClearsFrameIrqButNotDmcIrq()
    {
        var bus = new FlatRamBus();
        bus.Load(0xC000, 0x00);
        var apu = new Apu2A03(bus);
        apu.Dmc.WriteSampleAddress(0x00);
        apu.Dmc.WriteSampleLength(0x00);
        apu.Dmc.WriteControl(0x8F); // IRQ enable
        apu.WriteStatus(0x10); // enable DMC

        for (int i = 0; i < 8 * 60 * 2; i++) // *2 since DMC clocks every other Apu.Clock() call
        {
            apu.Clock();
        }
        Assert.True(apu.Dmc.IrqFlag);

        byte status = apu.ReadStatus();
        Assert.Equal(0x80, status & 0x80); // DMC IRQ was set
        Assert.True(apu.Dmc.IrqFlag); // ...and reading $4015 does not clear it
    }

    [Fact]
    public void Status_ReflectsWhichChannelsHaveActiveLengthCounters()
    {
        var apu = new Apu2A03(new FlatRamBus());
        apu.WriteStatus(0x00); // all disabled
        Assert.Equal(0x00, apu.ReadStatus() & 0x0F);

        apu.WriteStatus(0x01); // enable pulse1
        apu.Pulse1.WriteTimerHighAndLength(0x08); // load a nonzero length
        Assert.Equal(0x01, apu.ReadStatus() & 0x0F);
    }

    [Fact]
    public void Mixer_SilentChannels_ProduceZeroSamples()
    {
        var apu = new Apu2A03(new FlatRamBus());
        for (int i = 0; i < 100; i++)
        {
            apu.Clock();
        }

        Assert.NotEmpty(apu.SampleBuffer);
        Assert.All(apu.SampleBuffer, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void Mixer_ActivePulseChannel_ProducesNonZeroSamples()
    {
        var apu = new Apu2A03(new FlatRamBus());
        apu.WriteStatus(0x01);
        apu.Pulse1.WriteControl(0x1F); // constant volume, max volume, 50% duty... (duty bits also set here, fine)
        apu.Pulse1.WriteTimerLow(0x50);
        apu.Pulse1.WriteTimerHighAndLength(0x08);

        for (int i = 0; i < 2000; i++)
        {
            apu.Clock();
        }

        Assert.Contains(apu.SampleBuffer, sample => sample > 0f);
    }
}
