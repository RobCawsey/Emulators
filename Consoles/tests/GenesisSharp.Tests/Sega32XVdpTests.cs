using GenesisSharp.Core;

namespace GenesisSharp.Tests;

/// <summary>Phase 3 of the in-progress 32X extension (see ARCHITECTURE.md §4a.1): the frame
/// buffer/palette/display-mode register block and the compositing rule wired into
/// <see cref="Vdp.External32XPixelBlend"/>. Tests drive <see cref="Sega32X"/> directly via its
/// public surface (arrays, byte-level register methods, <see cref="Sega32X.TryGetPixel"/>) rather
/// than through a full <see cref="GenesisConsole"/>, mirroring how VdpTests.cs exercises
/// <see cref="Vdp"/> standalone.</summary>
public class Sega32XVdpTests
{
    private static Sega32X CreateSega32X()
    {
        var sega32X = new Sega32X(Cartridge.LoadFromBin(new byte[0x10000]));
        sega32X.Reset();
        return sega32X;
    }

    /// <summary>Writes a packed-pixel-mode frame ready to render one pixel: line table entry for
    /// <paramref name="line"/> points at word-offset 0, and the byte at that offset holds
    /// <paramref name="paletteIndex"/>.</summary>
    private static void SetUpPackedPixelLine(Sega32X sega32X, int line, byte paletteIndex)
    {
        sega32X.WriteVdpControlByteFrom68k(0x01, 0x01); // Mx = 1 (Packed Pixel)
        byte[] bank = sega32X.FrameBuffer[0]; // default DisplayBankIndex is 0 (FS=0 after reset)
        bank[line * 2] = 0x00;
        bank[line * 2 + 1] = 0x00; // line-table word offset = 0
        bank[0] = paletteIndex;
    }

    [Fact]
    public void TryGetPixel_DisplayModeOff_AlwaysReturnsFalse()
    {
        var sega32X = CreateSega32X(); // Mx = 0 is the power-on default

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: true, isH32: false, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetPixel_RunLengthMode_DegradesToLayerOffRatherThanRenderingGarbage()
    {
        var sega32X = CreateSega32X();
        sega32X.WriteVdpControlByteFrom68k(0x01, 0x03); // Mx = 3 (Run Length, deferred this phase)

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: true, isH32: false, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetPixel_PackedPixel_GenesisBackdrop_ShowsUnconditionallyRegardlessOfPriorityBit()
    {
        var sega32X = CreateSega32X();
        SetUpPackedPixelLine(sega32X, line: 8, paletteIndex: 5); // line-table index = y(0) + V28 offset(8)
        sega32X.Palette[5] = 0x0421; // priority bit (0x8000) clear

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: true, isH32: false, out byte r, out byte g, out byte b);

        Assert.True(result);
        Assert.NotEqual((0, 0, 0), (r, g, b)); // decoded from the palette entry, not left at zero
    }

    [Fact]
    public void TryGetPixel_PackedPixel_NonBackdropWithoutPriorityBit_GenesisPixelWins()
    {
        var sega32X = CreateSega32X();
        SetUpPackedPixelLine(sega32X, line: 8, paletteIndex: 5);
        sega32X.Palette[5] = 0x0421; // priority bit clear

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: false, isH32: false, out _, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetPixel_PackedPixel_NonBackdropWithPriorityBit_32XWins()
    {
        var sega32X = CreateSega32X();
        SetUpPackedPixelLine(sega32X, line: 8, paletteIndex: 5);
        sega32X.Palette[5] = 0x8421; // priority bit set

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: false, isH32: false, out _, out _, out _);

        Assert.True(result);
    }

    [Fact]
    public void TryGetPixel_PriRegister_InvertsThePrioritySense()
    {
        var sega32X = CreateSega32X();
        SetUpPackedPixelLine(sega32X, line: 8, paletteIndex: 5);
        sega32X.Palette[5] = 0x0421; // priority bit clear -- would normally lose to a non-backdrop Genesis pixel
        sega32X.WriteVdpControlByteFrom68k(0x01, (byte)(0x01 | 0x80)); // Mx=1, PRI bit also set

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: false, isH32: false, out _, out _, out _);

        Assert.True(result); // PRI flipped the clear bit to "set", so the 32X pixel now wins
    }

    [Fact]
    public void TryGetPixel_DirectColor_DecodesRawWordDirectlyNoPaletteInvolved()
    {
        var sega32X = CreateSega32X();
        sega32X.WriteVdpControlByteFrom68k(0x01, 0x02); // Mx = 2 (Direct Color)
        byte[] bank = sega32X.FrameBuffer[0];
        bank[8 * 2] = 0x00;
        bank[8 * 2 + 1] = 0x00; // line-table word offset = 0
        bank[0] = 0x84; // high byte: priority bit (0x80) set + top 2 bits of blue channel
        bank[1] = 0x21; // low byte: rest of the raw 5:5:5 value

        bool result = sega32X.TryGetPixel(0, 0, isGenesisBackdrop: false, isH32: false, out byte r, out byte g, out byte b);

        Assert.True(result); // priority bit was set in the raw word
        Assert.NotEqual((0, 0, 0), (r, g, b));
    }

    [Fact]
    public void TryGetPixel_H32Mode_OffsetsBySh2AndBlanksTheLeftmostFourColumns()
    {
        var sega32X = CreateSega32X();
        SetUpPackedPixelLine(sega32X, line: 8, paletteIndex: 5);
        sega32X.Palette[5] = 0x8421; // priority bit set, so it always shows once addressed correctly

        // Genesis x=0..3 have no corresponding 32X pixel in H32 mode (pixelX = x - 4 < 0).
        Assert.False(sega32X.TryGetPixel(0, 0, isGenesisBackdrop: false, isH32: true, out _, out _, out _));
        Assert.False(sega32X.TryGetPixel(3, 0, isGenesisBackdrop: false, isH32: true, out _, out _, out _));

        // Genesis x=4 maps to 32X frame-buffer pixel 0, where the palette-index-5 pixel lives.
        Assert.True(sega32X.TryGetPixel(4, 0, isGenesisBackdrop: false, isH32: true, out _, out _, out _));
    }

    [Fact]
    public void UpdateBlankingState_MirrorsVblkAndPenExactlyAsPicoDriveConfirmed()
    {
        var sega32X = CreateSega32X();
        sega32X.WriteVdpControlByteFrom68k(0x01, 0x01); // Mx = 1, so PEN genuinely clears on vblank-end

        sega32X.UpdateBlankingState(false); // first active scanline after reset -- exits the power-on blanking state
        Assert.Equal(0, sega32X.VdpRegs[5] & 0x8000); // VBLK clear
        Assert.Equal(0, sega32X.VdpRegs[5] & 0x2000); // PEN clear (Mx != 0)

        sega32X.UpdateBlankingState(true); // vblank starts again
        Assert.NotEqual(0, sega32X.VdpRegs[5] & 0x8000); // VBLK set
        Assert.NotEqual(0, sega32X.VdpRegs[5] & 0x2000); // PEN set
    }

    [Fact]
    public void FrameSelectSwap_AppliesImmediatelyWhileBlanking()
    {
        var sega32X = CreateSega32X(); // still in the power-on blanking state (VBLK set)

        sega32X.WriteVdpControlByteFrom68k(0x0B, 0x01); // request FS = 1

        Assert.NotEqual(0, sega32X.VdpRegs[5] & 0x0001); // applied right away
    }

    [Fact]
    public void FrameSelectSwap_DefersUntilVblankStartWhenNotBlanking()
    {
        var sega32X = CreateSega32X();
        sega32X.WriteVdpControlByteFrom68k(0x01, 0x01); // Mx = 1, so "Mx==0" no longer forces blanking
        sega32X.UpdateBlankingState(false); // exit blanking

        sega32X.WriteVdpControlByteFrom68k(0x0B, 0x01); // request FS = 1 while not blanking
        Assert.Equal(0, sega32X.VdpRegs[5] & 0x0001); // not yet applied

        sega32X.UpdateBlankingState(true); // vblank starts -- deferred swap now applies
        Assert.NotEqual(0, sega32X.VdpRegs[5] & 0x0001);
    }

    [Fact]
    public void Autofill_WritingFillDataFillsTheWriteTargetBank()
    {
        var sega32X = CreateSega32X();
        sega32X.WriteVdpControlByteFrom68k(0x04, 0x00);
        sega32X.WriteVdpControlByteFrom68k(0x05, 0x02); // fill length = 2 -> 3 words filled
        sega32X.WriteVdpControlByteFrom68k(0x06, 0x00);
        sega32X.WriteVdpControlByteFrom68k(0x07, 0x10); // fill start address = 0x10 (word offset)
        sega32X.WriteVdpControlByteFrom68k(0x08, 0x12);
        sega32X.WriteVdpControlByteFrom68k(0x09, 0x34); // fill data = 0x1234 -- triggers the burst

        // Default FS=0 -> display bank is 0, so the write-target (fill) bank is bank 1.
        byte[] writeBank = sega32X.FrameBuffer[1];
        for (int i = 0; i < 3; i++)
        {
            int byteOffset = (0x10 + i) * 2;
            Assert.Equal(0x12, writeBank[byteOffset]);
            Assert.Equal(0x34, writeBank[byteOffset + 1]);
        }
    }

    [Fact]
    public void PaletteWrite_GatedByFmBit_68kOwnsWhenFmClear()
    {
        var sega32X = CreateSega32X(); // FM clear (power-on default) -> 68000 owns

        sega32X.WritePaletteByteFrom68k(0x0A, 0x77);

        Assert.Equal(0x77, sega32X.ReadPaletteByteFor68k(0x0A));
    }

    [Fact]
    public void PaletteWrite_GatedByFmBit_68kWriteIgnoredWhenSh2Owns()
    {
        var sega32X = CreateSega32X();
        sega32X.WriteControlByteFrom68k(0x00, 0x80); // set FM -> SH-2 owns vdp_regs/palette now

        sega32X.WritePaletteByteFrom68k(0x0A, 0x77);

        Assert.Equal(0, sega32X.ReadPaletteByteFor68k(0x0A)); // write was ignored
    }

    [Fact]
    public void FrameBuffer_ByteWriteOfZero_IsSilentlyDroppedInBothWindows()
    {
        var sega32X = CreateSega32X();
        sega32X.FrameBuffer[1][0x100] = 0x42; // pre-existing value in the write-target bank

        sega32X.WriteFrameBufferByteFrom68k(0x100, 0x00);

        Assert.Equal(0x42, sega32X.ReadFrameBufferByteFor68k(0x100)); // zero write dropped, old value survives
    }

    [Fact]
    public void FrameBuffer_OverwriteWordWrite_MasksOutZeroBytesButNormalWriteDoesNot()
    {
        var sega32X = CreateSega32X();
        sega32X.FrameBuffer[1][0x200] = 0xAA;
        sega32X.FrameBuffer[1][0x201] = 0xBB;

        sega32X.WriteFrameBufferWordFrom68k(0x200, overwrite: true, 0x0044); // high byte 0x00 -> masked out

        Assert.Equal(0xAA, sega32X.FrameBuffer[1][0x200]); // old high byte survives
        Assert.Equal(0x44, sega32X.FrameBuffer[1][0x201]); // new low byte written

        sega32X.WriteFrameBufferWordFrom68k(0x200, overwrite: false, 0x0055); // normal window: unconditional

        Assert.Equal(0x00, sega32X.FrameBuffer[1][0x200]); // both bytes written even though high byte is zero
        Assert.Equal(0x55, sega32X.FrameBuffer[1][0x201]);
    }
}
