using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Ppu2C02RenderingTests
{
    private static void RunUntilFrameCount(Ppu2C02 ppu, long target)
    {
        while (ppu.FrameCount < target)
        {
            ppu.Tick();
        }
    }

    private static void FinishCurrentScanline(Ppu2C02 ppu)
    {
        int start = ppu.Scanline;
        while (ppu.Scanline == start)
        {
            ppu.Tick();
        }
    }

    private static void SetAddr(Ppu2C02 ppu, ushort addr)
    {
        ppu.WriteRegister(6, (byte)(addr >> 8));
        ppu.WriteRegister(6, (byte)(addr & 0xFF));
    }

    private static void WriteVram(Ppu2C02 ppu, ushort addr, byte value)
    {
        SetAddr(ppu, addr);
        ppu.WriteRegister(7, value);
    }

    /// <summary>Writes an 8x8 tile's pattern data (both bit planes) into CHR at the given
    /// tile index (table 0), such that every pixel in the tile has the given 2-bit value
    /// (0-3).</summary>
    private static void WriteSolidTile(Ppu2C02 ppu, int tileIndex, int pixelValue)
    {
        byte lo = (pixelValue & 1) != 0 ? (byte)0xFF : (byte)0x00;
        byte hi = (pixelValue & 2) != 0 ? (byte)0xFF : (byte)0x00;
        ushort baseAddr = (ushort)(tileIndex * 16);
        for (int row = 0; row < 8; row++)
        {
            WriteVram(ppu, (ushort)(baseAddr + row), lo);
            WriteVram(ppu, (ushort)(baseAddr + row + 8), hi);
        }
    }

    /// <summary>Parks all 64 sprites off-screen (Y=$FF). Real games always do this for unused
    /// OAM slots; without it, the default all-zero OAM would put 56 garbage sprites at Y=0,
    /// which — same as any real sprite — can land "in range" for a given scanline and skew
    /// sprite-count-sensitive assertions (overflow in particular).</summary>
    private static void ClearOamOffscreen(Ppu2C02 ppu)
    {
        ppu.WriteRegister(3, 0x00);
        for (int i = 0; i < 256; i++)
        {
            ppu.WriteRegister(4, 0xFF);
        }
    }

    private static void WriteOam(Ppu2C02 ppu, int spriteIndex, byte y, byte tile, byte attributes, byte x)
    {
        ppu.WriteRegister(3, (byte)(spriteIndex * 4));
        ppu.WriteRegister(4, y);
        ppu.WriteRegister(4, tile);
        ppu.WriteRegister(4, attributes);
        ppu.WriteRegister(4, x);
    }

    [Fact]
    public void SolidBackgroundTile_RendersMappedPaletteColor()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 0, pixelValue: 1);
        // Nametable defaults to all zeros (tile 0) already; attribute table also zero (palette 0).
        WriteVram(ppu, 0x3F00, 0x0F); // universal backdrop
        WriteVram(ppu, 0x3F01, 0x21); // background palette 0, pixel value 1

        ppu.WriteRegister(1, 0x1E); // PPUMASK: show background+sprites, incl. leftmost 8 px

        RunUntilFrameCount(ppu, 1); // warm up the fetch pipeline for one full frame
        FinishCurrentScanline(ppu); // render scanline 0 of frame 2

        for (int x = 0; x < 256; x++)
        {
            Assert.Equal(0x21, ppu.Frame[x]);
        }
    }

    [Fact]
    public void BackgroundDisabled_ShowsOnlyBackdropColor()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 0, pixelValue: 1);
        WriteVram(ppu, 0x3F00, 0x0F);
        WriteVram(ppu, 0x3F01, 0x21);

        ppu.WriteRegister(1, 0x00); // rendering fully disabled

        // With rendering disabled the fetch pipeline never runs, so no warm-up is needed —
        // and no frames advance either (odd-frame skip requires rendering to be enabled),
        // so just tick through one scanline directly.
        FinishCurrentScanline(ppu);

        for (int x = 0; x < 256; x++)
        {
            Assert.Equal(0x0F, ppu.Frame[x]);
        }
    }

    [Fact]
    public void Sprite_InFrontOfBackground_TakesPriority()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 0, pixelValue: 1); // background tile
        WriteSolidTile(ppu, tileIndex: 1, pixelValue: 2); // sprite tile
        WriteVram(ppu, 0x3F00, 0x0F);
        WriteVram(ppu, 0x3F01, 0x21); // bg palette 0, pixel 1
        // Sprite palette entries live at $3F10-$3F1F; palette 0 pixel-value-2 is $3F10+(0*4+2)=$3F12.
        WriteVram(ppu, 0x3F12, 0x02);

        WriteOam(ppu, 0, y: 0, tile: 1, attributes: 0x00, x: 0); // in front (bit5=0), palette 0
        ppu.WriteRegister(1, 0x1E);

        RunUntilFrameCount(ppu, 1);
        FinishCurrentScanline(ppu); // scanline 0 — sprite Y=0 means OAM_Y+1=1, so it's on scanline 1, not 0

        // The sprite's first visible row is scanline 1 (OAM Y + 1), so scanline 0 is pure background.
        Assert.Equal(0x21, ppu.Frame[0]);

        FinishCurrentScanline(ppu); // now render scanline 1

        Assert.Equal(0x02, ppu.Frame[256 + 0]); // sprite wins over background at (0,1)
    }

    [Fact]
    public void Sprite_BehindBackground_LosesPriorityToOpaqueBackground()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 0, pixelValue: 1); // opaque background
        WriteSolidTile(ppu, tileIndex: 1, pixelValue: 2); // sprite tile
        WriteVram(ppu, 0x3F00, 0x0F);
        WriteVram(ppu, 0x3F01, 0x21);
        WriteVram(ppu, 0x3F12, 0x02);

        WriteOam(ppu, 0, y: 0, tile: 1, attributes: 0x20, x: 0); // bit5=1: behind background
        ppu.WriteRegister(1, 0x1E);

        RunUntilFrameCount(ppu, 1);
        FinishCurrentScanline(ppu); // scanline 0 (sprite not visible here yet)
        FinishCurrentScanline(ppu); // scanline 1 — sprite's first visible row

        Assert.Equal(0x21, ppu.Frame[256 + 0]); // background wins since it's opaque and sprite is "behind"
    }

    [Fact]
    public void SpriteZero_OverlappingOpaqueBackground_SetsSpriteZeroHit()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 0, pixelValue: 1);
        WriteSolidTile(ppu, tileIndex: 1, pixelValue: 2);
        WriteVram(ppu, 0x3F00, 0x0F);
        WriteVram(ppu, 0x3F01, 0x21);
        WriteVram(ppu, 0x3F12, 0x02);

        WriteOam(ppu, 0, y: 0, tile: 1, attributes: 0x00, x: 0);
        ppu.WriteRegister(1, 0x1E);

        RunUntilFrameCount(ppu, 1);
        FinishCurrentScanline(ppu); // scanline 0
        Assert.Equal(0, ppu.ReadRegister(2) & 0x40); // not yet — sprite isn't visible until scanline 1

        FinishCurrentScanline(ppu); // scanline 1, sprite 0 overlaps opaque background here

        Assert.Equal(0x40, ppu.ReadRegister(2) & 0x40);
    }

    [Fact]
    public void MoreThanEightSpritesOnOneScanline_SetsOverflowFlag()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 1, pixelValue: 1);
        ClearOamOffscreen(ppu);
        for (int i = 0; i < 9; i++)
        {
            WriteOam(ppu, i, y: 0, tile: 1, attributes: 0x00, x: (byte)(i * 10));
        }
        ppu.WriteRegister(1, 0x1E);

        RunUntilFrameCount(ppu, 1);
        FinishCurrentScanline(ppu); // scanline 0: evaluates sprites for scanline 1, where all 9 are in range

        Assert.Equal(0x20, ppu.ReadRegister(2) & 0x20);
    }

    [Fact]
    public void EightOrFewerSpritesOnOneScanline_DoesNotSetOverflowFlag()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);
        WriteSolidTile(ppu, tileIndex: 1, pixelValue: 1);
        ClearOamOffscreen(ppu);
        for (int i = 0; i < 8; i++)
        {
            WriteOam(ppu, i, y: 0, tile: 1, attributes: 0x00, x: (byte)(i * 10));
        }
        ppu.WriteRegister(1, 0x1E);

        RunUntilFrameCount(ppu, 1);
        FinishCurrentScanline(ppu);

        Assert.Equal(0, ppu.ReadRegister(2) & 0x20);
    }
}
