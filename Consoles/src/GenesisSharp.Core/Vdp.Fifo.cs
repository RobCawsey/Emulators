namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    /// <summary>Models the VDP's 4-entry command FIFO and the 68000 bus-access "slots" it
    /// drains through. Confirmed against BlastEm's vdp.c (one of the few emulators accurate
    /// enough to pass Nemesis's VDP FIFO test ROM), which models this at real per-dot
    /// granularity: <c>FIFO_SIZE</c> is 4, and it distinguishes "external" (CPU/DMA-usable)
    /// access slots from the many more the VDP's own rendering fetches claim during active
    /// display. The CPU writing to the data port while 4 entries are already pending stalls
    /// until at least one drains — real hardware behavior this emulator previously didn't model
    /// at all (every write completed instantly; see the old comment this replaced on
    /// <see cref="ReadStatusRegister"/>'s FIFO bits).
    ///
    /// Confidence: HIGH on the FIFO depth (directly sourced from BlastEm). MEDIUM on <see
    /// cref="ExternalSlotsPerLineActive"/>/<see cref="ExternalSlotsPerLineBlanking"/> below:
    /// rather than BlastEm's full per-dot slot map, this collapses external-slot availability to
    /// one average rate for the whole scanline (matching this emulator's existing
    /// instruction-granularity timing, see <see cref="ScanlineProgress"/>), sized from published
    /// slot-count summaries (Plutiedev / the MegaDrive wiki) rather than BlastEm's own tables
    /// directly.</summary>
    public const int FifoDepth = 4;

    /// <summary>Access slots per line available to the CPU/DMA (as opposed to claimed by the
    /// VDP's own rendering fetches) — [blanking, mode]. Confirmed against genesis-plus-gx's
    /// <c>dma_timing[2][2]</c> table in vdp_ctrl.c (itself sourced from real hardware
    /// measurement): H32 gets fewer slots than H40 in both active display and blanking, a
    /// distinction this emulator previously collapsed into one flat rate per blanking state
    /// (18 active / 198 blanking, regardless of mode) — coincidentally exact for H40-active
    /// but off for H32 (really 16 active / 166 blanking) and for H40-blanking (really 204, not
    /// 198 — 198 is actually genesis-plus-gx's own further-adjusted value for a narrower case,
    /// CRAM/VSRAM destinations specifically, not a general blanking rate).</summary>
    private static readonly int[,] SlotsPerLineTable =
    {
        // H32, H40
        { 16, 18 },   // active display
        { 166, 204 }, // blanking (VBlank, or forced blank via register 1)
    };

    /// <summary>Matches GenesisConsole.CyclesPerScanlineM68000 (NTSC ~488 68000 cycles per
    /// scanline) — duplicated here rather than referenced across the assembly boundary since
    /// this is just the constant a scanline's external-slot budget is spread across.</summary>
    private const double CyclesPerScanline = 488.0;

    private readonly List<int> _fifoEntries = new();
    private double _slotBudget;
    private long _lastSlotClockCycles;
    private int _pendingStallCycles;
    private long _dmaBusyUntilCycle = long.MinValue;

    /// <summary>Whether a DMA transfer triggered via <see cref="ChargeDmaStall"/> is still
    /// "in progress" as of the most recent <see cref="AdvanceExternalSlotClock"/> call --
    /// backs <see cref="ReadStatusRegister"/>'s bit 1. Real hardware keeps this bit set for the
    /// transfer's actual duration; software that triggers a VRAM fill or VRAM-to-VRAM copy (the
    /// two DMA types that don't freeze the 68000 the way a 68k-bus-sourced transfer does, per
    /// <see cref="Vdp.Dma"/>'s remarks) is expected to poll this bit before touching the
    /// destination. This emulator used to hardcode the bit to a constant 0 ("no DMA-busy state
    /// to begin with") specifically to unblock a game that spun forever expecting it to clear --
    /// which it never could under the old *always-1* bug that fix replaced. Tracking a real
    /// busy-until cycle instead (rather than either hardcoded extreme) satisfies both: idle
    /// reads as not-busy like before, but a real transfer now reads as busy for its actual
    /// duration, matching what a polling loop like this would observe on real hardware.</summary>
    public bool IsDmaBusy => _lastSlotClockCycles < _dmaBusyUntilCycle;

    /// <summary>Diagnostic-only: how many more 68000 cycles <see cref="IsDmaBusy"/> will stay
    /// true, given the external-slot clock's most recent position. Not used by emulation itself
    /// (<see cref="IsDmaBusy"/> is the only thing <see cref="ReadStatusRegister"/> needs) -- added
    /// specifically so the debug window can show whether a game stuck polling this bit is waiting
    /// on a plausible remaining duration or one that's clearly wrong (e.g. an absurdly large DMA
    /// length read back garbled).</summary>
    public long DmaBusyRemainingCycles => Math.Max(0, _dmaBusyUntilCycle - _lastSlotClockCycles);

    /// <summary>Running total of every stall cycle ever charged (FIFO waits plus DMA), never
    /// reset by <see cref="ConsumeStallCycles"/> — debugging hook only, not part of emulation
    /// itself.</summary>
    public long TotalStallCyclesCharged { get; private set; }

    private int ExternalSlotsPerLine =>
        SlotsPerLineTable[(InVerticalBlank || !DisplayEnabled) ? 1 : 0, Is40CellMode ? 1 : 0];

    /// <summary>How many 68000 cycles pass, on average, between one externally-usable VDP
    /// access slot and the next at the current mode/blanking state — the CPU/DMA drain rate for
    /// the FIFO.</summary>
    private double CyclesPerExternalSlot => CyclesPerScanline / ExternalSlotsPerLine;

    private static int SlotsForTarget(VdpTarget target) => target == VdpTarget.Vram ? 2 : 1;

    private void ResetFifoState()
    {
        _fifoEntries.Clear();
        _slotBudget = 0;
        _lastSlotClockCycles = 0;
        _pendingStallCycles = 0;
        _dmaBusyUntilCycle = long.MinValue;
    }

    /// <summary>Called once per CPU instruction (before it executes) with the CPU's absolute
    /// cycle counter, so the FIFO drains by however much real time has actually passed since
    /// the last check — independent of whether that time was spent on ordinary instructions or
    /// an earlier stall this same method charged.</summary>
    public void AdvanceExternalSlotClock(long totalCpuCycles)
    {
        long elapsed = totalCpuCycles - _lastSlotClockCycles;
        _lastSlotClockCycles = totalCpuCycles;
        if (elapsed <= 0 || _fifoEntries.Count == 0)
        {
            return;
        }

        _slotBudget += elapsed / CyclesPerExternalSlot;
        DrainFifo();
    }

    private void DrainFifo()
    {
        while (_slotBudget >= 1.0 && _fifoEntries.Count > 0)
        {
            _slotBudget -= 1.0;
            _fifoEntries[0]--;
            if (_fifoEntries[0] <= 0)
            {
                _fifoEntries.RemoveAt(0);
            }
        }
    }

    /// <summary>Enqueues one FIFO entry for a data-port write, stalling the CPU first (adding to
    /// <see cref="ConsumeStallCycles"/>) if the FIFO is already full.</summary>
    private void EnqueueFifoEntry(VdpTarget target)
    {
        if (_fifoEntries.Count >= FifoDepth)
        {
            double slotsStillNeeded = _fifoEntries[0] - _slotBudget;
            if (slotsStillNeeded > 0)
            {
                double cyclesPerSlot = CyclesPerExternalSlot;
                int stallWhole = (int)Math.Ceiling(slotsStillNeeded * cyclesPerSlot);
                _pendingStallCycles += stallWhole;
                TotalStallCyclesCharged += stallWhole;
                _slotBudget += stallWhole / cyclesPerSlot;
                DrainFifo();
            }
        }

        _fifoEntries.Add(SlotsForTarget(target));
    }

    /// <summary>Diagnostic-only: how many times <see cref="ChargeDmaStall"/> has actually fired
    /// a new stall (i.e. <c>units &gt; 0</c>). Not used by emulation itself -- added to
    /// distinguish "one long DMA that just needs more real time" from "something is re-triggering
    /// DMA repeatedly, resetting the busy window before it ever expires" when a game's own polling
    /// loop appears stuck.</summary>
    public long DmaTriggerCount { get; private set; }

    /// <summary>Charges a flat stall for an entire DMA transfer up front, since this emulator
    /// runs DMA synchronously in one call rather than pacing it slot-by-slot the way real
    /// hardware (and BlastEm) do. <paramref name="units"/> is bytes for fill/VRAM-copy modes,
    /// words for the memory-to-VDP case; <paramref name="slotsPerUnit"/> is 2 for VRAM (one
    /// slot per byte, since VRAM is only 8 bits wide internally) or 1 for CRAM/VSRAM.</summary>
    private void ChargeDmaStall(int units, int slotsPerUnit)
    {
        if (units <= 0)
        {
            return;
        }

        DmaTriggerCount++;

        int stall = (int)Math.Ceiling(units * slotsPerUnit * CyclesPerExternalSlot);
        _pendingStallCycles += stall;
        TotalStallCyclesCharged += stall;
        _dmaBusyUntilCycle = _lastSlotClockCycles + stall;
    }

    /// <summary>Reads and resets the stall accumulated since the last call. GenesisConsole
    /// subtracts this from the CPU's per-scanline cycle debt right after <c>Cpu.Step()</c>, so
    /// the next instruction (or several, for a large DMA) effectively waits for it.</summary>
    public int ConsumeStallCycles()
    {
        int value = _pendingStallCycles;
        _pendingStallCycles = 0;
        return value;
    }
}
