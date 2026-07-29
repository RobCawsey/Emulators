using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class GamePadTests
{
    [Fact]
    public void Th1_ExposesDirectionsPlusBAndC()
    {
        var pad = new GamePad { Up = true, Right = true, B = true };

        byte lines = pad.ReadDataLines(th: true);

        Assert.Equal(0, lines & 0x01); // Up pressed -> line low
        Assert.Equal(0x02, lines & 0x02); // Down released -> line high
        Assert.Equal(0x04, lines & 0x04); // Left released -> line high
        Assert.Equal(0, lines & 0x08); // Right pressed -> line low
        Assert.Equal(0, lines & 0x10); // B pressed -> line low
        Assert.Equal(0x20, lines & 0x20); // C released -> line high
    }

    [Fact]
    public void Th0_ExposesUpDownPlusAAndStart_AndForcesD2D3Low()
    {
        var pad = new GamePad { Up = true, Left = true, Right = true, A = true };

        byte lines = pad.ReadDataLines(th: false);

        Assert.Equal(0, lines & 0x01); // Up pressed -> line low
        Assert.Equal(0x02, lines & 0x02); // Down released -> line high
        Assert.Equal(0, lines & 0x04); // D2 forced low regardless of Left being pressed
        Assert.Equal(0, lines & 0x08); // D3 forced low regardless of Right being pressed
        Assert.Equal(0, lines & 0x10); // A pressed -> line low
        Assert.Equal(0x20, lines & 0x20); // Start released -> line high
    }

    [Fact]
    public void NoButtonsPressed_AllLinesReadHigh()
    {
        var pad = new GamePad();

        Assert.Equal(0x3F, pad.ReadDataLines(th: true));
    }

    [Fact]
    public void Disconnected_AllLinesReadHighRegardlessOfTh()
    {
        var pad = new GamePad { Connected = false, Up = true, Down = true, A = true, Start = true };

        Assert.Equal(0x3F, pad.ReadDataLines(th: true));
        // The key detection signal: D2/D3 stay high here too, unlike a connected pad at TH=0.
        Assert.Equal(0x3F, pad.ReadDataLines(th: false));
    }

    [Fact]
    public void SixButton_Steps0Through2_BehaveExactlyLikeAThreeButtonPad()
    {
        var pad = new GamePad { SixButton = true, Up = true, B = true };

        Assert.Equal(0, pad.ReadDataLines(th: true, sequenceStep: 0) & 0x01);
        Assert.Equal(0, pad.ReadDataLines(th: false, sequenceStep: 1) & 0x01);
        Assert.Equal(0, pad.ReadDataLines(th: true, sequenceStep: 2) & 0x01);
    }

    [Fact]
    public void SixButton_Step3_ForcesAllFourDirectionBitsLow()
    {
        var pad = new GamePad { SixButton = true, Up = true, Left = true, A = true };

        byte lines = pad.ReadDataLines(th: false, sequenceStep: 3);

        Assert.Equal(0, lines & 0x0F); // D0-D3 all forced low, regardless of Up/Left
        Assert.Equal(0, lines & 0x10); // A still reported normally
        Assert.Equal(0x20, lines & 0x20); // Start released
    }

    [Fact]
    public void SixButton_Step4_ExposesXYZModeAtD0ThroughD3()
    {
        var pad = new GamePad { SixButton = true, X = true, Z = true, C = true };

        byte lines = pad.ReadDataLines(th: true, sequenceStep: 4);

        Assert.Equal(0, lines & 0x01); // X pressed
        Assert.Equal(0x02, lines & 0x02); // Y released
        Assert.Equal(0, lines & 0x04); // Z pressed
        Assert.Equal(0x08, lines & 0x08); // Mode released
        Assert.Equal(0x10, lines & 0x10); // B released (B/C still reported normally at D4/D5)
        Assert.Equal(0, lines & 0x20); // C pressed
    }

    [Fact]
    public void SixButton_Step5_ForcesAllFourDirectionBitsHigh()
    {
        var pad = new GamePad { SixButton = true };

        byte lines = pad.ReadDataLines(th: false, sequenceStep: 5);

        Assert.Equal(0x0F, lines & 0x0F);
    }

    [Fact]
    public void ThreeButtonPad_IgnoresTheExtendedStepsEntirely()
    {
        // Even asked for step 3/4/5, a plain 3-button pad just answers normally for the TH
        // level it's given -- it has no X/Y/Z/Mode and no "all forced" behavior at all.
        var pressingUp = new GamePad { SixButton = false, Up = true };
        Assert.Equal(0, pressingUp.ReadDataLines(th: false, sequenceStep: 3) & 0x01); // Up, not forced low as a group

        var pressingNothing = new GamePad { SixButton = false };
        Assert.Equal(0x3F, pressingNothing.ReadDataLines(th: true, sequenceStep: 4)); // no X/Y/Z/Mode -- normal, nothing pressed
    }
}
