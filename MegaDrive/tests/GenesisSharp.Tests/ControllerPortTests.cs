using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class ControllerPortTests
{
    [Fact]
    public void OutputConfiguredBits_EchoBackWhatWasLastWritten()
    {
        var port = new ControllerPort();
        port.Direction = 0x40; // only TH (bit 6) is an output, matching real software

        port.WriteData(0x40); // drive TH high

        Assert.Equal(0x40, port.ReadData() & 0x40);
    }

    [Fact]
    public void InputConfiguredBits_ReportThePadsSignalNotTheLastWrite()
    {
        var port = new ControllerPort();
        port.Direction = 0x40; // bits 0-5 are inputs
        port.Pad.Up = true;
        port.WriteData(0xFF); // driving bit 0 high should have no effect -- it's an input

        port.WriteData(0x40); // TH=1 (output)
        Assert.Equal(0, port.ReadData() & 0x01); // Up is pressed -> reads low regardless of the 0xFF write above
    }

    [Fact]
    public void Th_SwitchesWhichButtonGroupTheInputBitsExpose()
    {
        var port = new ControllerPort { Direction = 0x40 };
        port.Pad.B = true;
        port.Pad.A = true;

        port.WriteData(0x40); // TH=1
        Assert.Equal(0, port.ReadData() & 0x10); // B shows up at bit 4 when TH=1

        port.WriteData(0x00); // TH=0
        Assert.Equal(0, port.ReadData() & 0x10); // A shows up at bit 4 when TH=0 instead
    }

    [Fact]
    public void Bit7_AlwaysReadsHigh()
    {
        var port = new ControllerPort { Direction = 0x40 };

        Assert.Equal(0x80, port.ReadData() & 0x80);
    }

    [Fact]
    public void Reset_ClearsRegistersButNotButtonState()
    {
        var port = new ControllerPort { Direction = 0x40 };
        port.WriteData(0x40);
        port.Pad.Up = true;

        port.Reset();

        Assert.Equal(0, port.Direction);
        Assert.True(port.Pad.Up); // a physically-held button survives a console reset
    }

    [Fact]
    public void RapidThToggling_WalksThroughTheSixButtonDetectionSequence()
    {
        var port = new ControllerPort { Direction = 0x40 };
        port.Pad.SixButton = true;
        port.Pad.X = true;

        long cycle = 0;
        port.WriteData(0x40, cycle); // step 0, TH=1
        port.WriteData(0x00, cycle += 10); // step 1, TH=0
        port.WriteData(0x40, cycle += 10); // step 2, TH=1
        port.WriteData(0x00, cycle += 10); // step 3, TH=0 -- directions forced low
        Assert.Equal(0, port.ReadData() & 0x0F);

        port.WriteData(0x40, cycle += 10); // step 4, TH=1 -- X/Y/Z/Mode
        Assert.Equal(0, port.ReadData() & 0x01); // X pressed

        port.WriteData(0x00, cycle += 10); // step 5, TH=0 -- directions forced high
        Assert.Equal(0x0F, port.ReadData() & 0x0F);

        port.WriteData(0x40, cycle += 10); // back to step 0
        Assert.Equal(0x3F, port.ReadData() & 0x3F); // normal behavior resumed, nothing pressed
    }

    [Fact]
    public void SlowThToggling_ResetsTheSequenceInsteadOfAdvancingNormally()
    {
        // Up/Down both released, so step 1 (normal: D0/D1 high, D2/D3 forced low) and step 3
        // (forced: D0-D3 all low) are distinguishable -- if the long gap below were treated as
        // a normal transition instead of a reset, this would read as step 3's all-zero pattern.
        var port = new ControllerPort { Direction = 0x40 };
        port.Pad.SixButton = true;

        port.WriteData(0x40, 0); // step 0, TH=1
        port.WriteData(0x00, 10); // step 1, TH=0
        port.WriteData(0x40, 20); // step 2, TH=1
        port.WriteData(0x00, 20000); // gap far exceeds the timeout -- resets instead of advancing

        Assert.Equal(0x03, port.ReadData() & 0x0F);
    }

    [Fact]
    public void WritingTheSameThValueTwice_DoesNotAdvanceTheSequence()
    {
        var port = new ControllerPort { Direction = 0x40 };
        port.Pad.SixButton = true;

        port.WriteData(0x40, 0); // step 0
        port.WriteData(0x40, 10); // same TH again -- not a transition, doesn't count
        port.WriteData(0x00, 20); // only one real transition happened -> step 1, not step 3

        Assert.Equal(0x03, port.ReadData() & 0x0F); // step 1's pattern, not step 3's all-zero
    }
}
