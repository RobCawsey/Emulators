using System.Collections.Generic;

namespace NesSharp.Cpu;

/// <summary>
/// A cycle-stepped MOS 6502 core (as embedded in the NES's Ricoh 2A03 — same instruction
/// set and timing, no BCD arithmetic). <see cref="Clock"/> advances exactly one cycle;
/// callers drive the console's master clock and tick the PPU/APU alongside it, which is what
/// makes mid-instruction register side effects and DMA cycle-stealing possible to model.
///
/// Internally, decoding an opcode enqueues a short sequence of micro-op steps — one per
/// remaining cycle — onto <see cref="_uops"/>. Steps are plain instance-method delegates
/// closing over instance fields used as scratch registers (_addrLo, _effectiveAddr, etc.),
/// so addressing-mode logic (Cpu6502.Templates.cs) and control-flow opcodes
/// (Cpu6502.Special.cs) can be written as straight-line code instead of a hand-indexed
/// cycle/opcode switch. This favors clarity over allocation-free execution; if profiling
/// ever shows the small per-step delegate/closure allocations matter, the fix is to replace
/// the queue with a static per-opcode cycle-index switch — the addressing-mode timing tables
/// this file encodes don't change either way.
/// </summary>
public sealed partial class Cpu6502
{
    private readonly IBus _bus;
    private readonly Queue<Action> _uops = new(8);

    public byte A, X, Y, SP;
    public ushort PC;
    public CpuFlags P;

    /// <summary>Level-triggered IRQ input. Whatever asserts it (APU frame counter, DMC,
    /// a mapper) is responsible for clearing it — the CPU only samples it.</summary>
    public bool IrqLine;

    private bool _nmiPending;
    private OpcodeInfo _current;
    private OpKind _pendingKindForIndexed;

    // Micro-op scratch state, reused across opcodes.
    private byte _opcode;
    private byte _addrLo, _addrHi;
    private byte _ptr;
    private ushort _effectiveAddr;
    private ushort _guessAddr;
    private byte _fetched;
    private bool _pageCrossed;
    private bool _usesYIndex;

    public long TotalCycles { get; private set; }

    /// <summary>True between an opcode fetch and the last cycle of that instruction. A
    /// console driver that needs to tick the PPU/APU alongside the CPU one cycle at a time
    /// can still step whole instructions with <c>do { console.Clock(); } while (cpu.IsMidInstruction);</c>
    /// — the same shape as <see cref="RunUntilBoundary"/>, but through the console's own
    /// clock instead of the CPU's in isolation.</summary>
    public bool IsMidInstruction => _uops.Count > 0;

    /// <summary>Set when a JAM/KIL/HLT illegal opcode is executed. Real hardware locks up
    /// this way and needs a reset to recover — <see cref="Reset"/> clears this flag.</summary>
    public bool Jammed { get; private set; }

    public Cpu6502(IBus bus)
    {
        _bus = bus;
    }

    public bool GetFlag(CpuFlags flag) => (P & flag) != 0;

    public void SetFlag(CpuFlags flag, bool value) => P = value ? (P | flag) : (P & ~flag);

    private void SetZN(byte value)
    {
        SetFlag(CpuFlags.Zero, value == 0);
        SetFlag(CpuFlags.Negative, (value & 0x80) != 0);
    }

    private byte Read(ushort addr) => _bus.Read(addr);
    private void Write(ushort addr, byte value) => _bus.Write(addr, value);

    /// <summary>Edge-triggered: call once per falling edge of the PPU's NMI line.</summary>
    public void RaiseNmi() => _nmiPending = true;

    /// <summary>Halts the CPU for the given number of cycles (dummy reads at the current PC,
    /// matching real hardware) before the next instruction fetch. Used by OAMDMA, which
    /// takes the bus away from the CPU for 513/514 cycles. Queued after whatever instruction
    /// is currently mid-flight, since the write that triggers a DMA only takes effect once
    /// that instruction's own cycles are done.</summary>
    public void StallCycles(int cycles)
    {
        for (int i = 0; i < cycles; i++)
        {
            _uops.Enqueue(() => Read(PC));
        }
    }

    /// <summary>Advances the CPU by exactly one clock cycle.</summary>
    public void Clock()
    {
        TotalCycles++;
        if (_uops.Count == 0)
        {
            FetchAndDecode();
            return;
        }
        _uops.Dequeue().Invoke();
    }

    /// <summary>Test/debug convenience: clocks until back at an instruction boundary
    /// (queued micro-ops drained) and returns how many cycles that took. The real console
    /// loop should call <see cref="Clock"/> directly, one cycle at a time, so it can tick
    /// the PPU/APU alongside the CPU — this skips that and is only meant for driving the
    /// CPU core in isolation (unit tests, nestest-style log comparison).</summary>
    public int RunUntilBoundary()
    {
        long start = TotalCycles;
        do
        {
            Clock();
        } while (_uops.Count > 0);
        return (int)(TotalCycles - start);
    }

    /// <summary>7-cycle power-on/reset sequence: 2 dummy reads, 3 dummy stack decrements
    /// (no actual writes suppressed on real hardware), then the reset vector fetch. SP isn't
    /// preset — like real hardware, the 3 decrements just apply to whatever SP already holds,
    /// which lands on the conventional 0xFD only because SP starts at 0x00 on construction.</summary>
    public void Reset()
    {
        _uops.Clear();
        Jammed = false;
        P = CpuFlags.InterruptDisable | CpuFlags.Unused;
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => SP--);
        _uops.Enqueue(() => SP--);
        _uops.Enqueue(() => SP--);
        _uops.Enqueue(() => _addrLo = Read(0xFFFC));
        _uops.Enqueue(() => { _addrHi = Read(0xFFFD); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }

    private void FetchAndDecode()
    {
        if (Jammed)
        {
            // Real hardware just gets stuck re-reading the same address forever until reset.
            Read(PC);
            return;
        }

        // Interrupts are only sampled at an instruction boundary; NMI takes priority over IRQ.
        if (_nmiPending)
        {
            _nmiPending = false;
            BeginNmi();
            return;
        }
        if (IrqLine && !GetFlag(CpuFlags.InterruptDisable))
        {
            BeginIrq();
            return;
        }

        _opcode = Read(PC++);

        switch (_opcode)
        {
            case 0x00: BuildBrk(); return;
            case 0x20: BuildJsr(); return;
            case 0x60: BuildRts(); return;
            case 0x40: BuildRti(); return;
            case 0x4C: BuildJmpAbsolute(); return;
            case 0x6C: BuildJmpIndirect(); return;
            case 0x48: BuildPha(); return;
            case 0x08: BuildPhp(); return;
            case 0x68: BuildPla(); return;
            case 0x28: BuildPlp(); return;

            // JAM/KIL/HLT — undocumented, freezes the CPU on real hardware.
            case 0x02: case 0x12: case 0x22: case 0x32:
            case 0x42: case 0x52: case 0x62: case 0x72:
            case 0x92: case 0xB2: case 0xD2: case 0xF2:
                Jammed = true;
                PC--; // stay parked on the jam opcode, as real hardware does
                return;
        }

        var info = OpcodeTable.Table[_opcode] ?? throw new UnimplementedOpcodeException(_opcode);
        _current = info;

        switch (info.Kind)
        {
            case OpKind.Branch:
                BuildBranch();
                break;
            case OpKind.Implied:
                BuildImplied();
                break;
            default:
                BuildAddressed(info.Mode, info.Kind);
                break;
        }
    }
}
