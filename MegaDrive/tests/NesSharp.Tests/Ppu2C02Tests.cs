using NesSharp.Core;
using Xunit;

namespace NesSharp.Tests;

public class Ppu2C02Tests
{
    private static void TickUntil(Ppu2C02 ppu, int scanline, int dot)
    {
        while (ppu.Scanline != scanline || ppu.Dot != dot)
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

    /// <summary>Reads the true current value at a non-palette VRAM address, accounting for
    /// PPUDATA's one-read-behind buffering (see Ppu2C02.ReadPpuData).</summary>
    private static byte ReadVram(Ppu2C02 ppu, ushort addr)
    {
        SetAddr(ppu, addr);
        ppu.ReadRegister(7); // primes the buffer with addr's value
        SetAddr(ppu, addr); // undo the auto-increment from the priming read
        return ppu.ReadRegister(7);
    }

    [Fact]
    public void VBlank_SetsAtScanline241Dot1()
    {
        var ppu = new Ppu2C02(new FakeMapper());

        TickUntil(ppu, 241, 1);

        Assert.Equal(0x80, ppu.ReadRegister(2) & 0x80);
    }

    [Fact]
    public void VBlank_ClearsAtPrerenderScanline261Dot1_EvenWithoutBeingRead()
    {
        var ppu = new Ppu2C02(new FakeMapper());

        TickUntil(ppu, 241, 1); // sets VBlank
        TickUntil(ppu, 261, 1); // pre-render clears it automatically

        Assert.Equal(0, ppu.ReadRegister(2) & 0x80);
    }

    [Fact]
    public void ReadingStatus_ClearsVBlankFlag()
    {
        var ppu = new Ppu2C02(new FakeMapper());
        TickUntil(ppu, 241, 1);

        Assert.Equal(0x80, ppu.ReadRegister(2) & 0x80); // first read observes it set...
        Assert.Equal(0, ppu.ReadRegister(2) & 0x80);    // ...and clears it
    }

    [Fact]
    public void ReadingStatus_ResetsAddressLatchMidSequence()
    {
        var ppu = new Ppu2C02(new FakeMapper());

        ppu.WriteRegister(6, 0x99); // first byte of a two-write PPUADDR sequence
        ppu.ReadRegister(2);        // interrupts it — latch should reset to "expect high byte"

        // If the latch hadn't reset, 0x25 would complete a pair with the stray 0x99
        // (landing on some other address), and 0x00 would start a new one — never
        // producing $2500 the way a clean two-write sequence does.
        ppu.WriteRegister(6, 0x25);
        ppu.WriteRegister(6, 0x00);
        ppu.WriteRegister(7, 0xAB);

        Assert.Equal(0xAB, ReadVram(ppu, 0x2500));
    }

    [Fact]
    public void Nmi_FiresAtVBlankStart_WhenAlreadyEnabled()
    {
        var ppu = new Ppu2C02(new FakeMapper());
        ppu.WriteRegister(0, 0x80); // enable NMI in PPUCTRL

        TickUntil(ppu, 241, 1);

        Assert.True(ppu.TakeNmiRequest());
        Assert.False(ppu.TakeNmiRequest()); // one-shot
    }

    [Fact]
    public void Nmi_DoesNotFire_WhenDisabled()
    {
        var ppu = new Ppu2C02(new FakeMapper());

        TickUntil(ppu, 241, 1);

        Assert.False(ppu.TakeNmiRequest());
    }

    [Fact]
    public void Nmi_FiresImmediately_WhenEnabledWhileVBlankAlreadySet()
    {
        var ppu = new Ppu2C02(new FakeMapper());
        TickUntil(ppu, 241, 1); // VBlank sets with NMI still disabled
        Assert.False(ppu.TakeNmiRequest());

        ppu.WriteRegister(0, 0x80); // enabling now should fire immediately (hardware quirk)

        Assert.True(ppu.TakeNmiRequest());
    }

    [Fact]
    public void PpuData_NonPaletteReads_AreDelayedByOneBufferedRead()
    {
        var ppu = new Ppu2C02(new FakeMapper());
        WriteVram(ppu, 0x2000, 0x55);

        SetAddr(ppu, 0x2000);
        byte first = ppu.ReadRegister(7); // returns the stale (default) buffer, not 0x55 yet
        byte second = ppu.ReadRegister(7); // now returns 0x55, buffered by the first read

        Assert.Equal(0x00, first);
        Assert.Equal(0x55, second);
    }

    [Fact]
    public void PpuData_PaletteReads_AreImmediate()
    {
        var ppu = new Ppu2C02(new FakeMapper());
        WriteVram(ppu, 0x3F00, 0x30);

        SetAddr(ppu, 0x3F00);
        byte value = ppu.ReadRegister(7);

        Assert.Equal(0x30, value);
    }

    [Fact]
    public void Palette_MirrorsSpriteBackgroundEntriesToUniversalBackground()
    {
        var ppu = new Ppu2C02(new FakeMapper());
        WriteVram(ppu, 0x3F00, 0x0F);

        Assert.Equal(0x0F, ReadVram(ppu, 0x3F10));
    }

    [Fact]
    public void Nametable_HorizontalMirroring_SharesTablesInPairs()
    {
        var ppu = new Ppu2C02(new FakeMapper());

        WriteVram(ppu, 0x2000, 0x11);
        WriteVram(ppu, 0x2800, 0x22);

        Assert.Equal(0x11, ReadVram(ppu, 0x2400)); // $2000 mirrors $2400
        Assert.Equal(0x22, ReadVram(ppu, 0x2C00)); // $2800 mirrors $2C00
        Assert.Equal(0x11, ReadVram(ppu, 0x2000)); // independent physical table, unaffected
    }

    [Fact]
    public void Nametable_VerticalMirroring_SharesTablesInPairs()
    {
        var ppu = new Ppu2C02(new FakeMapper { Mirroring = MirroringMode.Vertical });

        WriteVram(ppu, 0x2000, 0x33);
        WriteVram(ppu, 0x2400, 0x44);

        Assert.Equal(0x33, ReadVram(ppu, 0x2800)); // $2000 mirrors $2800
        Assert.Equal(0x44, ReadVram(ppu, 0x2C00)); // $2400 mirrors $2C00
    }

    [Fact]
    public void PatternTable_RoutesThroughMapper()
    {
        var mapper = new FakeMapper();
        var ppu = new Ppu2C02(mapper);

        WriteVram(ppu, 0x0123, 0x9A);

        Assert.Equal(0x9A, mapper.PpuRead(0x0123));
    }
}
