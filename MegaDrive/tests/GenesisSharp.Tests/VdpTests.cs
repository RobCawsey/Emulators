using GenesisSharp.Core;

namespace GenesisSharp.Tests;

public class VdpTests
{
    private static ushort RegisterWriteWord(int register, byte data) => (ushort)(0x8000 | (register << 8) | data);

    [Fact]
    public void ControlPort_RegisterWrite_UpdatesTypedAccessor()
    {
        var vdp = new Vdp();

        vdp.WriteControlPort(RegisterWriteWord(1, 0x40)); // display enable

        Assert.True(vdp.DisplayEnabled);
        Assert.Equal(0x40, vdp.Registers[1]);
    }

    [Fact]
    public void Mode5Enabled_IsRegisterOneBitTwo_NotBitThree()
    {
        // Regression test for a real bug: this was originally implemented against bit 3,
        // which is actually the 30-cell/240-line select bit -- not Mode 5 select. Found by
        // running a real homebrew ROM through the emulator: its very first VDP register 1
        // write was 0x14 (DMA enable + this bit, bit 3 deliberately left clear). Every one of
        // this project's own tests had been unknowingly reinforcing the wrong bit, since none
        // of them combined this with register 0's M4 bit the way real game code does.
        var vdp = new Vdp();

        vdp.Registers[1] = 0x04;
        Assert.True(vdp.Mode5Enabled);

        vdp.Registers[1] = 0x08;
        Assert.False(vdp.Mode5Enabled);

        vdp.Registers[1] = 0x14; // DMA enable + Mode 5 -- the real ROM's actual first write
        Assert.True(vdp.Mode5Enabled);
    }

    [Fact]
    public void DataPort_VramRoundTrip_WithAutoIncrement()
    {
        var vdp = new Vdp();
        vdp.WriteControlPort(RegisterWriteWord(15, 2)); // auto-increment = 2

        // First control word: code=1 (VRAM write) in bits 15-14, address 0x1000 in bits 13-0.
        vdp.WriteControlPort((ushort)((1 << 14) | 0x1000));
        vdp.WriteControlPort(0x0000); // second word: no high address/code bits needed
        vdp.WriteDataPort(0x1234);
        vdp.WriteDataPort(0x5678); // lands at 0x1002 thanks to auto-increment

        Assert.Equal(0x12, vdp.Vram[0x1000]);
        Assert.Equal(0x34, vdp.Vram[0x1001]);
        Assert.Equal(0x56, vdp.Vram[0x1002]);
        Assert.Equal(0x78, vdp.Vram[0x1003]);

        // Read back: code=0 (VRAM read) this time.
        vdp.WriteControlPort(0x1000);
        vdp.WriteControlPort(0x0000);
        Assert.Equal(0x1234, vdp.ReadDataPort());
        Assert.Equal(0x5678, vdp.ReadDataPort());
    }

    [Fact]
    public void DataPort_CramWrite_UpdatesCramArray()
    {
        var vdp = new Vdp();

        // Code=3 (CRAM write): low 2 bits of code (11) go in the first word's top bits.
        vdp.WriteControlPort((ushort)(0x03 << 14));
        vdp.WriteControlPort(0x0000);
        vdp.WriteDataPort(0x00E0);

        Assert.Equal(0x00E0, vdp.Cram[0]);
    }

    [Fact]
    public void DataPort_VsramWrite_UpdatesVsramArray()
    {
        var vdp = new Vdp();

        // Code=5 (VSRAM write): low 2 bits (01) in the first word, CD2 (the only high bit set)
        // at bit 4 of the second word -- matches the well-known VSRAM_ADDR_CMD constant
        // ($40000010)'s low word.
        vdp.WriteControlPort((ushort)(0x01 << 14));
        vdp.WriteControlPort(0x0010);
        vdp.WriteDataPort(0x0123);

        Assert.Equal(0x0123, vdp.Vsram[0]);
    }

    [Fact]
    public void Dma_VramFill_FillsSequentialBytesAndLeavesLengthRegisterAsLastWritten()
    {
        // Found via real-ROM testing (Omega Blast): the length registers are plain CPU-write
        // latches on real hardware, not auto-clearing counters -- a ROM that re-triggers a DMA
        // of the same size without rewriting the length register expects it to still hold the
        // value it last wrote. This emulator used to zero registers 19/20 after every transfer,
        // which turned exactly that "repeat the same-size transfer" idiom into a phantom
        // zero-length DMA -- and this emulator's own (correct, hardware-matching) zero-length
        // handling then reads that as "transfer 65536 bytes," flooding VRAM with the fill/source
        // byte and wiping out real graphics that were already loaded.
        var vdp = new Vdp();
        vdp.WriteControlPort(RegisterWriteWord(1, 0x10)); // DMA enable
        vdp.WriteControlPort(RegisterWriteWord(15, 1)); // auto-increment = 1
        vdp.WriteControlPort(RegisterWriteWord(19, 4)); // DMA length = 4
        vdp.WriteControlPort(RegisterWriteWord(20, 0));
        vdp.WriteControlPort(RegisterWriteWord(23, 0x80)); // fill mode

        // Destination address 0x2000, code = 0x21 (VRAM write | DMA bit).
        vdp.WriteControlPort((ushort)((0x21 & 0x3) << 14 | 0x2000));
        vdp.WriteControlPort((ushort)(((0x21 >> 2) & 0xF) << 4));
        vdp.WriteDataPort(0x00AB); // fill byte = 0xAB; this write triggers the fill, not a normal write

        Assert.Equal(0xAB, vdp.Vram[0x2000]);
        Assert.Equal(0xAB, vdp.Vram[0x2001]);
        Assert.Equal(0xAB, vdp.Vram[0x2002]);
        Assert.Equal(0xAB, vdp.Vram[0x2003]);
        Assert.Equal(4, vdp.DmaLength); // retained, not auto-cleared
    }

    [Fact]
    public void Dma_MemoryToVram_CopiesFromExternalReadDelegate()
    {
        var vdp = new Vdp { ExternalMemoryRead = address => (byte)(address & 0xFF) };
        vdp.WriteControlPort(RegisterWriteWord(1, 0x10)); // DMA enable
        vdp.WriteControlPort(RegisterWriteWord(15, 2)); // auto-increment = 2
        vdp.WriteControlPort(RegisterWriteWord(19, 2)); // DMA length = 2 words
        vdp.WriteControlPort(RegisterWriteWord(20, 0));
        vdp.WriteControlPort(RegisterWriteWord(21, 0x10)); // source word address 0x10 -> byte 0x20
        vdp.WriteControlPort(RegisterWriteWord(22, 0));
        vdp.WriteControlPort(RegisterWriteWord(23, 0x00)); // memory-to-VDP mode

        // Destination 0x3000, code = 0x21 (VRAM write | DMA bit) — triggers immediately (not fill mode).
        vdp.WriteControlPort((ushort)((0x21 & 0x3) << 14 | 0x3000));
        vdp.WriteControlPort((ushort)(((0x21 >> 2) & 0xF) << 4));

        Assert.Equal(0x20, vdp.Vram[0x3000]);
        Assert.Equal(0x21, vdp.Vram[0x3001]);
        Assert.Equal(0x22, vdp.Vram[0x3002]);
        Assert.Equal(0x23, vdp.Vram[0x3003]);
    }

    [Fact]
    public void Dma_VramToVram_CopiesWithinVram()
    {
        var vdp = new Vdp();
        vdp.Vram[0x100] = 0xAA;
        vdp.Vram[0x101] = 0xBB;
        vdp.Vram[0x102] = 0xCC;
        vdp.Vram[0x103] = 0xDD;

        vdp.WriteControlPort(RegisterWriteWord(1, 0x10));
        vdp.WriteControlPort(RegisterWriteWord(15, 1));
        vdp.WriteControlPort(RegisterWriteWord(19, 4));
        vdp.WriteControlPort(RegisterWriteWord(20, 0));
        vdp.WriteControlPort(RegisterWriteWord(21, 0x80)); // source word address 0x80 -> byte 0x100
        vdp.WriteControlPort(RegisterWriteWord(22, 0));
        vdp.WriteControlPort(RegisterWriteWord(23, 0xC0)); // VRAM copy mode

        // Destination 0x0400 — must stay within the first control word's 14-bit address
        // field (0-0x3FFF); 0x4000 itself would overflow into the code bits.
        vdp.WriteControlPort((ushort)((0x21 & 0x3) << 14 | 0x0400));
        vdp.WriteControlPort((ushort)(((0x21 >> 2) & 0xF) << 4));

        Assert.Equal(0xAA, vdp.Vram[0x0400]);
        Assert.Equal(0xBB, vdp.Vram[0x0401]);
        Assert.Equal(0xCC, vdp.Vram[0x0402]);
        Assert.Equal(0xDD, vdp.Vram[0x0403]);
    }

    [Fact]
    public void AdvanceScanline_ReachingScreenHeight_FiresVerticalBlankAndSetsStatusFlag()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x20; // enable vertical interrupts
        bool raised = false;
        vdp.VerticalBlankStarted += () => raised = true;

        for (int line = 0; line < Vdp.ScreenHeight - 1; line++)
        {
            vdp.AdvanceScanline();
            Assert.False(vdp.InVerticalBlank);
        }

        vdp.AdvanceScanline();

        Assert.True(raised);
        Assert.True(vdp.InVerticalBlank);
        Assert.Equal(0x0088, vdp.ReadStatusRegister() & 0x0088); // F flag + VBlank flag
        Assert.Equal(0x0008, vdp.ReadStatusRegister() & 0x0088); // F flag cleared by the read above, VBlank (live level) still set
    }

    [Fact]
    public void ReadStatusRegister_DmaBusyBitDefaultsClear_NotSet()
    {
        // Found via real-ROM testing: a homebrew game (Scorpion Illuminati) spins forever at
        // boot on BTST #1,D1 / BNE waiting for this bit to read 0 -- confirmed by direct
        // experiment (flipping the default let the same ROM boot straight through to real
        // gameplay). Bit1 is genuinely "DMA busy" per Nemesis's documentation; this emulator
        // completes every DMA synchronously within the triggering write, so it's never busy.
        var vdp = new Vdp();

        Assert.Equal(0, vdp.ReadStatusRegister() & 0x0002);
    }

    [Fact]
    public void Render_SingleTileOnPlaneA_ProducesExpectedPixelColor()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40; // display enable
        vdp.Registers[4] = 1; // Plane B base = 0x2000, kept empty/transparent
        vdp.Registers[13] = 1; // H-scroll table base = 0x400, kept zeroed

        // Plane A name table entry 0 (address 0): tile index 1, palette line 0, no flip/priority.
        vdp.Vram[0] = 0x00;
        vdp.Vram[1] = 0x01;

        // Tile 1's data (address 32): every pixel is color index 5.
        for (int i = 0; i < 32; i++)
        {
            vdp.Vram[32 + i] = 0x55;
        }

        vdp.Cram[5] = 0x000E; // palette line 0, color 5: pure red (R=7,G=0,B=0 in 3-bit channels)

        vdp.RenderScanline(0);

        Assert.Equal(255, vdp.FrameBuffer[0]); // R
        Assert.Equal(0, vdp.FrameBuffer[1]);   // G
        Assert.Equal(0, vdp.FrameBuffer[2]);   // B
    }

    [Fact]
    public void Render_TransparentPlaneAPixel_FallsThroughToBackgroundColor()
    {
        var vdp = new Vdp();
        vdp.Registers[1] = 0x40;
        vdp.Registers[4] = 1;
        vdp.Registers[13] = 1;
        vdp.Registers[7] = 0x02; // background color index 2, palette line 0
        vdp.Cram[2] = 0x0E00; // bits 9-11 set -> B=7, R=G=0: pure blue

        // Plane A tile 0 (the VRAM default) is all zeros — every pixel is color index 0 (transparent).

        vdp.RenderScanline(0);

        Assert.Equal(0, vdp.FrameBuffer[0]);
        Assert.Equal(0, vdp.FrameBuffer[1]);
        Assert.Equal(255, vdp.FrameBuffer[2]);
    }

    [Fact]
    public void Render_DisplayDisabled_ProducesBlankScanline()
    {
        var vdp = new Vdp();
        vdp.Vram[0] = 0xFF; // would show something if display were enabled
        vdp.Cram[0] = 0x0FFF;

        vdp.RenderScanline(0);

        Assert.All(vdp.FrameBuffer[..(Vdp.ScreenWidth * 3)], b => Assert.Equal(0, b));
    }
}
