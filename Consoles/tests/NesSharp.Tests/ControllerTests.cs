using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class ControllerTests
{
    [Fact]
    public void WhileStrobed_EveryRead_ReportsButtonA()
    {
        var controller = new Controller();
        controller.SetButtons(NesButtons.A | NesButtons.Start);
        controller.Write(0x01); // strobe held high

        Assert.Equal(1, controller.Read());
        Assert.Equal(1, controller.Read());
        Assert.Equal(1, controller.Read());
    }

    [Fact]
    public void AfterStrobeReleased_ReadsShiftOutButtonsInOrder()
    {
        var controller = new Controller();
        // A, Start, Right pressed — bits 0, 3, 7.
        controller.SetButtons(NesButtons.A | NesButtons.Start | NesButtons.Right);
        controller.Write(0x01);
        controller.Write(0x00); // latch the state, begin shifting

        int[] expected = { 1, 0, 0, 1, 0, 0, 0, 1 }; // A,B,Select,Start,Up,Down,Left,Right
        foreach (int expectedBit in expected)
        {
            Assert.Equal(expectedBit, controller.Read());
        }
    }

    [Fact]
    public void ReadsPastTheEighth_ReturnOne()
    {
        var controller = new Controller();
        controller.SetButtons(NesButtons.None);
        controller.Write(0x01);
        controller.Write(0x00);

        for (int i = 0; i < 8; i++)
        {
            controller.Read();
        }

        Assert.Equal(1, controller.Read());
        Assert.Equal(1, controller.Read());
    }

    [Fact]
    public void ButtonChangesDuringStrobe_AreReflectedImmediately()
    {
        var controller = new Controller();
        controller.Write(0x01);
        controller.SetButtons(NesButtons.B);

        Assert.Equal(0, controller.Read()); // A not pressed
        controller.SetButtons(NesButtons.A);
        Assert.Equal(1, controller.Read()); // now A is pressed
    }
}
