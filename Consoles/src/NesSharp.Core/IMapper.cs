namespace NesSharp.Core;

/// <summary>A cartridge's mapper, seen from both the CPU bus ($4020-$FFFF) and the PPU bus
/// (pattern tables, $0000-$1FFF).</summary>
public interface IMapper
{
    byte CpuRead(ushort address);
    void CpuWrite(ushort address, byte value);
    byte PpuRead(ushort address);
    void PpuWrite(ushort address, byte value);

    /// <summary>Current nametable mirroring. Fixed (from the cartridge header) for simple
    /// mappers; mappers with their own mirroring control (e.g. MMC1) report it live here,
    /// and Ppu2C02 queries this on every nametable access rather than caching a value.</summary>
    MirroringMode Mirroring { get; }

    /// <summary>Called by Ppu2C02 on every PPU bus access (the full 14-bit address, before
    /// any nametable-mirroring resolution) — the real PPU address bus signal that mappers
    /// like MMC3 watch bit 12 of (their "A12" line) to drive a scanline IRQ counter. A no-op
    /// default so mappers without IRQ hardware don't need to implement it.</summary>
    void NotifyA12(ushort ppuAddress)
    {
    }

    /// <summary>Level-triggered IRQ output from the mapper itself (distinct from the APU's).
    /// False by default for mappers with no IRQ hardware.</summary>
    bool IrqLine => false;

    /// <summary>Called once per CPU cycle by NesConsole, for mappers whose IRQ counter is
    /// clocked by CPU cycles rather than the PPU's A12 line (VRC6, VRC7, Sunsoft FME-7 —
    /// unlike MMC3/MMC2). A no-op default for mappers without a cycle-driven IRQ.</summary>
    void ClockCpu()
    {
    }

    /// <summary>This cycle's expansion audio output, mixed directly into the APU's analog
    /// sum (see Apu2A03.Clock) alongside the 2A03's own five channels. 0 by default for
    /// mappers with no expansion audio. Expected to be roughly the same scale as the 2A03
    /// mixer's own output (0..~1) — exact per-board mixing levels/impedance aren't modeled.</summary>
    float GetAudioSample() => 0f;
}
