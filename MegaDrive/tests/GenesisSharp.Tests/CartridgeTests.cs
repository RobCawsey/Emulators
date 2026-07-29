using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class CartridgeTests
{
    [Fact]
    public void LoadFromBin_PreservesRomBytes()
    {
        byte[] rom = [0x01, 0x02, 0x03, 0x04];

        Cartridge cartridge = Cartridge.LoadFromBin(rom);

        Assert.Equal(rom, cartridge.Rom);
    }
}
