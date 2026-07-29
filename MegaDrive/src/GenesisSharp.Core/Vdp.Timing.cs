namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    public int CurrentScanline { get; private set; }
    public bool InVerticalBlank => CurrentScanline >= ScreenHeight;

    private bool _verticalInterruptPending;
    private bool _spriteOverflowPending;
    private bool _spriteCollisionPending;
    private int _hInterruptCountdown;

    /// <summary>0.0 at the start of the current scanline's CPU cycle budget, approaching 1.0
    /// at the end — fed in by GenesisConsole's frame loop once per 68000 instruction (see
    /// <c>GenesisConsole.CyclesPerScanlineM68000</c>) so a mid-scanline HV-counter read or
    /// HBlank check (see Vdp.HvCounter.cs) reflects roughly where the CPU actually is within
    /// the line. This is instruction-granularity, not true dot-clock precision.</summary>
    public double ScanlineProgress { get; private set; }

    public void SetScanlineProgress(double progress) => ScanlineProgress = progress < 0 ? 0 : progress > 1 ? 1 : progress;

    private void ResetTimingState()
    {
        CurrentScanline = 0;
        _verticalInterruptPending = false;
        _spriteOverflowPending = false;
        _spriteCollisionPending = false;
        CurrentFieldIsOdd = false;
        _hInterruptCountdown = HInterruptCounter;
        ScanlineProgress = 0;
    }

    /// <summary>Advances by one scanline — GenesisConsole's frame loop calls this once per
    /// ~488 CPU cycles (NTSC). Firing <see cref="VerticalBlankStarted"/> here is what lets
    /// GenesisConsole.RequestVerticalBlankInterrupt reach both CPUs at the right moment;
    /// wrapping back to scanline 0 is also what toggles <see cref="CurrentFieldIsOdd"/> for
    /// IM2. The H-interrupt countdown only ticks during the active display (not blanking) —
    /// real hardware's exact behavior around the countdown during vblank isn't nailed down
    /// here, so this is the common simplified version other emulators also start from.</summary>
    public void AdvanceScanline()
    {
        CurrentScanline++;

        if (CurrentScanline < ScreenHeight)
        {
            _hInterruptCountdown--;
            if (_hInterruptCountdown < 0)
            {
                _hInterruptCountdown = HInterruptCounter;
                if (HorizontalInterruptEnabled)
                {
                    HorizontalInterruptRequested?.Invoke();
                }
            }
        }

        if (CurrentScanline == ScreenHeight)
        {
            _verticalInterruptPending = true;
            if (VerticalInterruptEnabled)
            {
                VerticalBlankStarted?.Invoke();
            }
        }

        if (CurrentScanline >= LinesPerFrame)
        {
            CurrentScanline = 0;
            CurrentFieldIsOdd = !CurrentFieldIsOdd;
            _hInterruptCountdown = HInterruptCounter;
        }

        ScanlineProgress = 0;
    }

    /// <summary>The control port's read value. Bit layout per Nemesis's widely-cited,
    /// authoritative reverse-engineered documentation (confirmed via web search after two
    /// separate real-ROM bugs turned up from an earlier, badly-scrambled guess at this table —
    /// see below): bit0 PAL/NTSC (hardcoded 0 — this emulator is NTSC-only throughout), bit1
    /// DMA busy, bit2 HBlank, bit3 VBlank, bit4 odd-frame (interlace), bit5 sprite collision,
    /// bit6 sprite overflow, bit7 vertical-interrupt-pending ("F") flag, bit8 FIFO full, bit9
    /// FIFO empty — both now reflect the real 4-entry FIFO queue (see Vdp.Fifo.cs) rather than
    /// the hardcoded "always empty" this emulator started with. Bits 2/3/4/8/9 are live levels;
    /// 1/5/6/7 are latched flags cleared by this read.
    ///
    /// Bit1 (see <see cref="IsDmaBusy"/>) used to be hardcoded to a constant 0 -- "this emulator
    /// has no DMA-busy state to begin with" -- specifically to unblock a game (Scorpion
    /// Illuminati) that spun forever expecting it to clear under the even-earlier *always-1*
    /// bug. Neither hardcoded extreme is real hardware behavior: SGDK's VDP_waitDMACompletion
    /// (and by extension, any homebrew built with it) polls this exact bit expecting it to read
    /// busy for a VRAM fill/copy's real duration and then clear -- a real-ROM comparison against
    /// genesis-plus-gx (Omega Blast's title screen) showed this timing gap was significant
    /// enough to change the game's own subsequent VRAM-address bookkeeping. Now tracks a real
    /// busy-until-cycle window instead, set by <see cref="ChargeDmaStall"/> to the transfer's
    /// already-computed real duration -- idle still reads not-busy, but an actual transfer now
    /// reads busy for its real duration, satisfying both games at once.
    ///
    /// This table used to have bits 2/3/4/6/7 scrambled relative to their real positions —
    /// found via two real-ROM regressions (Scorpion Illuminati spinning on bit1 expecting DMA
    /// idle, which the old "always 1" default could never satisfy; Omega Blast spinning
    /// forever on a bit3 wait-for-vblank-edge idiom, because bit3 was wired to sprite overflow
    /// instead of VBlank). Both are explained cleanly by this corrected table instead of by
    /// one-off polarity patches.</summary>
    public ushort ReadStatusRegister()
    {
        ushort value = 0;
        if (_fifoEntries.Count == 0) value |= 0x0200; // bit9 FIFO empty
        if (_fifoEntries.Count >= FifoDepth) value |= 0x0100; // bit8 FIFO full
        if (IsDmaBusy) value |= 0x0002;
        if (IsInHorizontalBlank) value |= 0x0004;
        if (InVerticalBlank) value |= 0x0008;
        if (CurrentFieldIsOdd) value |= 0x0010;
        if (_spriteCollisionPending) value |= 0x0020;
        if (_spriteOverflowPending) value |= 0x0040;
        if (_verticalInterruptPending) value |= 0x0080;

        _spriteCollisionPending = false;
        _spriteOverflowPending = false;
        _verticalInterruptPending = false;
        return value;
    }

    public ushort ReadControlPort() => ReadStatusRegister();
}
