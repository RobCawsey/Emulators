namespace NesSharp.Cpu;

/// <summary>
/// Control-flow opcodes whose cycle sequence doesn't fit the generic Read/Write/Modify/
/// Implied/Branch template shapes, plus the two hardware interrupt entry sequences (NMI/IRQ),
/// which are BRK's push/vector sequence minus the padding-byte fetch and without the Break
/// flag being set in the pushed status.
/// </summary>
public sealed partial class Cpu6502
{
    private ushort StackAddr => (ushort)(0x0100 + SP);

    // ---- BRK (7 cycles) ----

    private void BuildBrk()
    {
        _uops.Enqueue(() => { Read(PC); PC++; }); // padding byte, discarded
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC >> 8)); SP--; });
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC & 0xFF)); SP--; });
        _uops.Enqueue(() =>
        {
            Write(StackAddr, (byte)(P | CpuFlags.Break | CpuFlags.Unused));
            SP--;
            SetFlag(CpuFlags.InterruptDisable, true);
        });
        _uops.Enqueue(() => _addrLo = Read(0xFFFE));
        _uops.Enqueue(() => { _addrHi = Read(0xFFFF); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }

    // ---- JSR (6 cycles) ----

    private void BuildJsr()
    {
        _uops.Enqueue(() => _addrLo = Read(PC++));
        _uops.Enqueue(() => Read(StackAddr)); // internal cycle, real hardware touches the stack here
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC >> 8)); SP--; });
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC & 0xFF)); SP--; });
        _uops.Enqueue(() => { _addrHi = Read(PC); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }

    // ---- RTS (6 cycles) ----

    private void BuildRts()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => Read(StackAddr));
        _uops.Enqueue(() => { SP++; _addrLo = Read(StackAddr); });
        _uops.Enqueue(() => { SP++; _addrHi = Read(StackAddr); PC = (ushort)((_addrHi << 8) | _addrLo); });
        _uops.Enqueue(() => PC++);
    }

    // ---- RTI (6 cycles) ----

    private void BuildRti()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => Read(StackAddr));
        _uops.Enqueue(() => { SP++; P = PulledStatus(Read(StackAddr)); });
        _uops.Enqueue(() => { SP++; _addrLo = Read(StackAddr); });
        _uops.Enqueue(() => { SP++; _addrHi = Read(StackAddr); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }

    // ---- JMP absolute (3 cycles) ----

    private void BuildJmpAbsolute()
    {
        _uops.Enqueue(() => _addrLo = Read(PC++));
        _uops.Enqueue(() => { _addrHi = Read(PC++); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }

    // ---- JMP indirect (5 cycles) — reproduces the page-wrap bug: the pointer's high byte
    // fetch wraps within the same page instead of carrying, so JMP ($xxFF) misreads $xx00. ----

    private void BuildJmpIndirect()
    {
        _uops.Enqueue(() => _addrLo = Read(PC++));
        _uops.Enqueue(() => _addrHi = Read(PC++));
        _uops.Enqueue(() =>
        {
            _effectiveAddr = (ushort)((_addrHi << 8) | _addrLo);
            _fetched = Read(_effectiveAddr);
        });
        _uops.Enqueue(() =>
        {
            ushort hiPtr = (ushort)((_effectiveAddr & 0xFF00) | (byte)((_effectiveAddr & 0xFF) + 1));
            byte hi = Read(hiPtr);
            PC = (ushort)((hi << 8) | _fetched);
        });
    }

    // ---- PHA / PHP (3 cycles) ----

    private void BuildPha()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => { Write(StackAddr, A); SP--; });
    }

    private void BuildPhp()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => { Write(StackAddr, (byte)(P | CpuFlags.Break | CpuFlags.Unused)); SP--; });
    }

    // ---- PLA / PLP (4 cycles) ----

    private void BuildPla()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => Read(StackAddr));
        _uops.Enqueue(() => { SP++; A = Read(StackAddr); SetZN(A); });
    }

    private void BuildPlp()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => Read(StackAddr));
        _uops.Enqueue(() => { SP++; P = PulledStatus(Read(StackAddr)); });
    }

    /// <summary>Bits 4 (Break) and 5 (Unused) of the status register have no real storage in
    /// the 6502 — they're only synthesized at push time (PHP/BRK force Break=1, hardware
    /// IRQ/NMI force it 0; Unused always reads 1). PLP/RTI pulling a byte with bit 4 set does
    /// not make it "stick" — the very next PHP still forces it back to 1 regardless. Modeling
    /// that: every PLP/RTI forces Break off and Unused on, discarding whatever bit 4/5 were in
    /// the pulled byte, so our push-time forcing is the only place those bits ever come from.</summary>
    private static CpuFlags PulledStatus(byte pulled) =>
        ((CpuFlags)pulled & ~CpuFlags.Break) | CpuFlags.Unused;

    // ---- Hardware interrupts (7 cycles each) ----

    private void BeginNmi()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC >> 8)); SP--; });
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC & 0xFF)); SP--; });
        _uops.Enqueue(() =>
        {
            Write(StackAddr, (byte)(P | CpuFlags.Unused));
            SP--;
            SetFlag(CpuFlags.InterruptDisable, true);
        });
        _uops.Enqueue(() => _addrLo = Read(0xFFFA));
        _uops.Enqueue(() => { _addrHi = Read(0xFFFB); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }

    private void BeginIrq()
    {
        _uops.Enqueue(() => Read(PC));
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC >> 8)); SP--; });
        _uops.Enqueue(() => { Write(StackAddr, (byte)(PC & 0xFF)); SP--; });
        _uops.Enqueue(() =>
        {
            Write(StackAddr, (byte)(P | CpuFlags.Unused));
            SP--;
            SetFlag(CpuFlags.InterruptDisable, true);
        });
        _uops.Enqueue(() => _addrLo = Read(0xFFFE));
        _uops.Enqueue(() => { _addrHi = Read(0xFFFF); PC = (ushort)((_addrHi << 8) | _addrLo); });
    }
}
