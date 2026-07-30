using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class Ym2612Tests
{
    [Fact]
    public void WritePart1_LatchesAddressThenStoresDataAtThatAddress()
    {
        var ym = new Ym2612();

        ym.WriteAddressPart1(0x28); // key-on/off register
        ym.WriteDataPart1(0xF0);

        Assert.Equal(0xF0, ym.ReadRegisterPart1(0x28));
    }

    [Fact]
    public void WritePart2_IsIndependentOfPart1()
    {
        var ym = new Ym2612();

        ym.WriteAddressPart1(0x30);
        ym.WriteDataPart1(0x11);
        ym.WriteAddressPart2(0x30);
        ym.WriteDataPart2(0x22);

        Assert.Equal(0x11, ym.ReadRegisterPart1(0x30));
        Assert.Equal(0x22, ym.ReadRegisterPart2(0x30));
    }

    [Fact]
    public void ReWritingTheAddressLatch_RedirectsSubsequentDataWrites()
    {
        var ym = new Ym2612();

        ym.WriteAddressPart1(0x28);
        ym.WriteDataPart1(0xAA);
        ym.WriteAddressPart1(0x29);
        ym.WriteDataPart1(0xBB);

        Assert.Equal(0xAA, ym.ReadRegisterPart1(0x28));
        Assert.Equal(0xBB, ym.ReadRegisterPart1(0x29));
    }

    [Fact]
    public void ReadStatus_AlwaysReportsNotBusyNoOverflow()
    {
        var ym = new Ym2612();
        ym.WriteAddressPart1(0x28);
        ym.WriteDataPart1(0xF0);

        Assert.Equal(0, ym.ReadStatus());
    }

    [Fact]
    public void Reset_ClearsBothRegisterBanksAndLatchedAddresses()
    {
        var ym = new Ym2612();
        ym.WriteAddressPart1(0x28);
        ym.WriteDataPart1(0xF0);
        ym.WriteAddressPart2(0x30);
        ym.WriteDataPart2(0x11);

        ym.Reset();

        Assert.Equal(0, ym.ReadRegisterPart1(0x28));
        Assert.Equal(0, ym.ReadRegisterPart2(0x30));

        // The address latch itself resets to 0 too -- writing data with no fresh address
        // write lands at register 0.
        ym.WriteDataPart1(0x42);
        Assert.Equal(0x42, ym.ReadRegisterPart1(0x00));
    }
}
