using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpH32Tests
{
    private static void FillTile(Vdp vdp, int tileIndex, byte colorIndex)
    {
        byte b = (byte)((colorIndex << 4) | colorIndex);
        for (int i = 0; i < 32; i++)
        {
            vdp.Vram[tileIndex * 32 + i] = b;
        }
    }

    [Fact]
    public void ActiveWidth_ReflectsRegister12Bit0()
    {
        var vdp = new Vdp();

        Assert.Equal(256, vdp.ActiveWidth); // H32 is the power-on default (register 12 = 0)

        vdp.Registers[12] = 0x01;
        Assert.Equal(320, vdp.ActiveWidth);
    }

    [Fact]
    public void H32_RendersOnlyTheFirst256ColumnsAndBlanksTheRest()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable, H32 by default (register 12 = 0)
        vdp.Registers[7] = 0x01; // background color index 1
        vdp.Cram[1] = 0x000E; // red backdrop

        // Prime the frame buffer's inactive tail with non-zero bytes first, so the blank step
        // has something to actually prove it cleared.
        int rowOffset = 0;
        for (int i = 0; i < Vdp.ScreenWidth * 3; i++)
        {
            vdp.FrameBuffer[rowOffset + i] = 0xFF;
        }

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]); // column 0: backdrop red, actively rendered
        Assert.Equal(255, vdp.FrameBuffer[255 * 3]); // column 255: still active
        Assert.Equal(0, vdp.FrameBuffer[256 * 3]); // column 256: blanked, not backdrop red
        Assert.Equal(0, vdp.FrameBuffer[256 * 3 + 1]);
        Assert.Equal(0, vdp.FrameBuffer[256 * 3 + 2]);
        Assert.Equal(0, vdp.FrameBuffer[319 * 3]); // column 319: also blanked
    }

    [Fact]
    public void H40_RendersTheFullWidthWithNoBlankedTail()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[12] = 0x01; // H40
        vdp.Registers[7] = 0x01;
        vdp.Cram[1] = 0x000E;

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[319 * 3]); // last H40 column is actively rendered, not blanked
    }

    [Fact]
    public void H32_SpriteOverflow_TriggersAtSixteenNotTwenty()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // H32 by default
        FillTile(vdp, 2, colorIndex: 1);
        vdp.Cram[1] = 0x000E;

        for (int i = 0; i < 17; i++)
        {
            int link = i == 16 ? 0 : i + 1;
            uint address = vdp.SpriteTableBase + (uint)(i * 8);
            ushort word0 = (ushort)((0 + 128) & 0x03FF);
            ushort word1 = (ushort)(link & 0x7F);
            ushort word2 = 2;
            ushort word3 = (ushort)((i * 8 + 128) & 0x03FF);
            vdp.Vram[address] = (byte)(word0 >> 8); vdp.Vram[address + 1] = (byte)word0;
            vdp.Vram[address + 2] = (byte)(word1 >> 8); vdp.Vram[address + 3] = (byte)word1;
            vdp.Vram[address + 4] = (byte)(word2 >> 8); vdp.Vram[address + 5] = (byte)word2;
            vdp.Vram[address + 6] = (byte)(word3 >> 8); vdp.Vram[address + 7] = (byte)word3;
        }

        vdp.RenderScanline(0);

        int sprite16Offset = (16 * 8) * 3; // the 17th sprite (index 16) — past H32's 16-sprite limit
        Assert.Equal(0, vdp.FrameBuffer[sprite16Offset]);
        Assert.Equal(0x0040, vdp.ReadStatusRegister() & 0x0040); // overflow flag set
    }

    [Fact]
    public void WindowRowStride_DiffersBetweenH32AndH40()
    {
        var h32 = new Vdp();
        h32.Registers[1] = 0x40;
        var h40 = new Vdp();
        h40.Registers[1] = 0x40;
        h40.Registers[12] = 0x01;

        h32.Registers[18] = 0x80; // window covers every row
        h40.Registers[18] = 0x80;

        // Entry at tileX=1, tileY=1: address = base + (tileY*stride + tileX) * 2.
        // H32 (stride 32): base + 66. H40 (stride 64): base + 130.
        WriteWord(h32, h32.WindowNameTableBase + 66, 5);
        WriteWord(h40, h40.WindowNameTableBase + 130, 5);
        FillTile(h32, 5, colorIndex: 1);
        FillTile(h40, 5, colorIndex: 1);
        h32.Cram[1] = 0x000E;
        h40.Cram[1] = 0x000E;

        h32.RenderScanline(8); // tile row 1
        h40.RenderScanline(8);

        Assert.Equal(255, h32.FrameBuffer[(8 * Vdp.ScreenWidth + 8) * 3]); // tileX=1 -> x=8-15
        Assert.Equal(255, h40.FrameBuffer[(8 * Vdp.ScreenWidth + 8) * 3]);
    }

    private static void WriteWord(Vdp vdp, uint address, ushort value)
    {
        vdp.Vram[address] = (byte)(value >> 8);
        vdp.Vram[address + 1] = (byte)value;
    }
}
