# GenesisSharp Architecture

GenesisSharp is a from-scratch C# emulator for the Sega Genesis / Mega Drive. This document
describes how it's built, why it's built that way, and where to look when you want to extend it.
It is written for a developer who has never opened this codebase before.

This document covers **GenesisSharp only**. The repository also contains an unrelated NES
emulator (`NesSharp*`) that shares no code with GenesisSharp and is out of scope here.

> **A note on confidence.** Emulating undocumented 1988 hardware from memory and community
> knowledge means not everything here is equally certain. Throughout the codebase (and this
> document) you'll see explicit confidence flags: some behavior is "confirmed against
> genesis-plus-gx source" or "verified against a hardware test-vector suite"; some is "recalled
> from general documentation, not independently verified"; a little is "an honest approximation,
> not a fabricated hardware-exact value." Where a subsystem's confidence is notably lower than the
> rest (VDP Mode 4, interlace mode, the H-counter's dot-exact timing), this document says so. When
> extending the emulator, treat low-confidence areas as the first place to look if a game misbehaves,
> and treat "confirmed against X" claims as a starting point for your own verification, not gospel.

---

## Table of contents

1. [High-level architecture](#1-high-level-architecture)
2. [Solution and project layout](#2-solution-and-project-layout)
3. [The 68000 CPU core](#3-the-68000-cpu-core)
4. [The Z80 CPU core](#4-the-z80-cpu-core)
4a. [The SH-2 CPU core (32X, in progress)](#4a-the-sh-2-cpu-core-32x-in-progress)
5. [GenesisConsole — the system bus and orchestrator](#5-genesisconsole--the-system-bus-and-orchestrator)
6. [The VDP (Video Display Processor)](#6-the-vdp-video-display-processor)
7. [Audio: YM2612 (FM) and PSG](#7-audio-ym2612-fm-and-psg)
8. [Save states](#8-save-states)
9. [The WinForms frontend](#9-the-winforms-frontend)
10. [Testing strategy](#10-testing-strategy)
11. [How to extend the emulator](#11-how-to-extend-the-emulator)
12. [Known limitations and low-confidence areas](#12-known-limitations-and-low-confidence-areas)
13. [References and credits](#13-references-and-credits)

---

## 1. High-level architecture

A real Genesis is built around two CPUs sharing access to a set of memory-mapped chips. GenesisSharp
mirrors that shape directly rather than inventing its own abstraction:

```
                         ┌─────────────────────────┐
                         │     GenesisConsole       │  <- implements BOTH IBus interfaces;
                         │   (the "motherboard")    │     this is where the memory map lives
                         └───────────┬──────────────┘
             ┌───────────────────────┼────────────────────────┐
             │                       │                        │
      ┌──────▼──────┐         ┌──────▼──────┐          ┌──────▼──────┐
      │   M68000    │         │     Vdp     │          │     Z80     │
      │ (main CPU)  │◄───────►│ (video +    │◄────────►│ (sound CPU) │
      └─────────────┘  ports  │  DMA/FIFO)  │  bus win. └──────┬──────┘
                               └─────────────┘                  │
                                                          ┌──────┴──────┐
                                                          │Ym2612 / Psg │
                                                          │ (sound gen) │
                                                          └─────────────┘
```

`GenesisConsole` (`src/GenesisSharp.Core/GenesisConsole.cs`) is the single class that decodes
memory addresses for **both** CPUs. Neither CPU core knows anything about the Genesis memory
map — each is handed `GenesisConsole` itself as an opaque `IBus` at construction time, and the
CPU cores only ever call `ReadByte`/`WriteByte`/etc. This is the most important structural fact
in the codebase: **if you're adding a new memory-mapped device, you add it to `GenesisConsole`'s
bus decode, not to either CPU.**

Five projects implement this:

| Project | Role |
|---|---|
| `GenesisSharp.Cpu68000` | Motorola 68000 core — main CPU, knows nothing about the Genesis |
| `GenesisSharp.CpuZ80` | Zilog Z80 core — sound CPU, knows nothing about the Genesis |
| `GenesisSharp.Core` | The Genesis itself: bus/memory map, VDP, YM2612, PSG, controllers, save states |
| `GenesisSharp.Frontend` | WinForms shell: window, input, audio playback, debugger UI |
| `GenesisSharp.Tests` | xUnit test suite (387 tests as of this writing) |

Execution is a **unified per-scanline cycle-budget loop** (see §5.4) rather than either CPU
running freely: each scanline, both CPUs are advanced exactly as many cycles as real hardware's
clock would allow, VDP DMA/FIFO stalls are charged against the 68000, and audio samples are
generated at cycle-accurate points within the same loop. There is no separate "audio thread pull"
model reaching into live chip state — samples are pre-generated into a queue at the moment they
happen in emulated time (§5.6), which is what lets save-state resume and audio timing both stay
correct.

---

## 2. Solution and project layout

```
GenesisSharp.sln
src/
  GenesisSharp.Cpu68000/   — net9.0, no dependencies
  GenesisSharp.CpuZ80/     — net9.0, no dependencies
  GenesisSharp.CpuSh2/     — net9.0, no dependencies (32X Phase 1)
  GenesisSharp.Core/       — net9.0, depends on Cpu68000 + CpuZ80 + CpuSh2 (32X Phase 2)
  GenesisSharp.Frontend/   — net9.0-windows, WinForms, depends on Core, NAudio 2.3.0
tests/
  GenesisSharp.Tests/      — net9.0, xUnit, depends on Cpu68000 + CpuZ80 + CpuSh2 + Core (not Frontend)
SagaRoms/                  — real commercial/homebrew ROMs used by integration tests & manual testing
reference/
  genesis-plus-gx/         — cloned reference emulator, used only for source comparison (never built/linked)
  SGDK/                    — cloned homebrew SDK, used only for source comparison
  PicoDrive/picodrive/     — cloned reference emulator, ground truth for the SH-2/32X work (§4a)
```

Dependency direction is strictly one-way: `Cpu68000`/`CpuZ80`/`CpuSh2` → `Core` → `Frontend`.
`Frontend` never references any CPU project directly — it only ever sees `Cpu68000.IBus`/
`CpuZ80.IBus` (aliased in `DebugForm.cs` as `Cpu68000Bus`/`Z80Bus`) through `Core`; there's no
`CpuSh2`-facing frontend support yet (see §4a's known gaps). `Tests` references every CPU project
and `Core` directly (for low-level per-instruction unit tests) but not `Frontend`, since
`MainForm`/`DebugForm` have no automated coverage — see §10.

`reference/genesis-plus-gx`, `reference/SGDK`, and `reference/PicoDrive` are **not build
dependencies of anything** — they are git clones kept around purely so that comments in this
codebase can say "confirmed against genesis-plus-gx's `vdp_ctrl.c`" (or PicoDrive's `sh2.c`) and
you can go read that file yourself. See §13 for their licenses.

Every project has `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`, set
per-project (there's no shared `Directory.Build.props`). `GenesisSharp.Frontend` is the only
project targeting `net9.0-windows` (`OutputType=WinExe`, `UseWindowsForms=true`), and the only one
with a NuGet dependency: **NAudio 2.3.0**, used exclusively for `WasapiOut` audio playback.

---

## 3. The 68000 CPU core

`src/GenesisSharp.Cpu68000/`, class `M68000` — a `public sealed partial class` split across ~19
files by instruction group (e.g. `M68000.Move.cs`, `M68000.Shift.cs`, `M68000.XBcd.cs`,
`M68000.Privileged.cs`; see the file for the full list). It is a general-purpose 68000 emulator —
nothing Genesis-specific lives here.

### Bus abstraction

```csharp
public interface IBus
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadLong(uint address);
    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteLong(uint address, uint value);
}
```
The CPU is constructed with `new M68000(IBus bus)` and stores it as a single `readonly` field.
`GenesisConsole` implements this interface; that's the entire coupling between the two projects.

### Decode strategy

Top-level dispatch is a `switch` on the opcode's top nibble, matching Motorola's own coarse
opcode map, with per-nibble sub-dispatchers below that mix plain switches with mask/pattern
matching for irregular encodings (e.g. the "miscellaneous group 0100" that holds NEG, JSR, JMP,
LEA, PEA, CLR, TST, MOVEM, CHK, TRAP). A handful of instructions (SBCD/ABCD/ADDX/SUBX/EXG) nest
*inside* what would otherwise be another instruction's encoding space and are disambiguated by
small bit-pattern predicate helpers (`IsSbcd`, `IsSubx`, `IsExg`, etc.) checked before falling
through to the "plain" path.

### Interrupts

`RaiseInterrupt(int level)` (levels 1–7) only raises the CPU's pending level if the new request is
higher than whatever's already pending — modeling the fact that a real priority encoder presents
only the single highest currently-asserted IPL. Every `Step()` call services any qualifying
pending interrupt *before* fetching, exactly matching hardware's between-instruction IPL sampling
(checked even while the CPU is `Stopped` from a STOP instruction). Servicing is always
**autovectored**: `PC = bus.ReadLong((24 + level) * 4)` — there's no vectored-interrupt-ack cycle
model, which is fine because the Genesis wires nothing that needs one.

### Save state

`M68000.SaveState.cs` — writes all 8 data + 8 address registers, `PC`, `SR`, the shadow USP,
`TotalCycles`, `Stopped`, and `PendingInterruptLevel`. Safe to call between any two `Step()` calls
because the CPU decodes and executes an instruction atomically within one `Step()` — there's no
partial-instruction state to lose (contrast with the Z80, below).

### Known gaps and quirks (all deliberate, all documented in-file)

- CMPM (postincrement-postincrement compare) is not decoded separately and incorrectly falls
  through to the EOR path — a known, tracked gap (`M68000.Compare.cs`).
- NEGX and address/bus-error traps are unimplemented; hitting that encoding throws.
- RTE does not check supervisor privilege.
- `0x4AFC` is special-cased as the dedicated illegal-instruction opcode rather than falling into
  TAS's decode range — found because Sonic 1 genuinely executes it as a "should never run" trap.
- The ADDX/SUBX vs. ADDA.L/SUBA.L bit-pattern collision is explicitly excluded via an opmode check
  — found via real-ROM testing when ordinary `SUBA.L` code was being misrouted into SUBX.
- DIVU/DIVS timing (140/158 cycles) is the commonly-cited average-case figure, not the real
  bit-dependent iterative-division formula — an acknowledged approximation.
- ABCD/SBCD leave the V flag alone (officially undefined) rather than guessing.
- MOVE USP does not auto-swap SSP/USP on supervisor-mode transitions (irrelevant in practice — real
  Genesis code stays in supervisor mode essentially always).
- Line A / Line F opcodes deliberately trap correctly (vectors 10/11) rather than being treated as
  "unimplemented" — some ROMs execute them on purpose as anti-piracy/emulator-detection checks.
- DIVU/DIVS divide-by-zero and overflow flag behavior (N/Z forced, not left alone) was cross-checked
  against **clown68000**, itself validated against the **SingleStepTests/m68000** hardware test-vector
  suite (`M68000.Logic.cs`) — see §13.

---

## 4. The Z80 CPU core

`src/GenesisSharp.CpuZ80/`, class `Z80` — split across ~11 files (`Z80.Main.cs`,
`Z80.Main.Explicit.cs`, `Z80.Cb.cs`, `Z80.Ed.cs`, `Z80.Indexed.cs`, etc.). Also a general-purpose
core with no Genesis-specific knowledge.

### Bus abstraction

```csharp
public interface IBus
{
    byte ReadByte(ushort address);
    void WriteByte(ushort address, byte value);
    byte ReadPort(byte port);
    void WritePort(byte port, byte value);
}
```
Narrower than the 68000's — byte-only memory access plus a separate port-space pair for
`IN`/`OUT`/`INI`/`OUTI`/etc. On the real Genesis, Z80 port I/O is entirely unwired; `GenesisConsole`
implements `ReadPort`/`WritePort` as unconditional `0xFF`/no-op.

### Decode strategy — the key structural idea

Unlike the 68000 core's mostly hand-written switch tree, the Z80 core explicitly decodes its large
*regular* opcode grids (LD r,r′; ALU A,r; INC/DEC r; LD rp,nn; PUSH/POP; Jcc/CALLcc/RETcc; RST;
ALU A,n; and more) by **mask-and-shift arithmetic** instead of spelling out 100+ near-duplicate
switch arms, and reserves an explicit `switch` (`ExecuteMainOpcodeExplicit`) only for genuinely
irregular opcodes. The CB-prefixed table is similarly fully regular and decoded via 2-bit
group + 3-bit y + 3-bit z field extraction with no lookup table at all.

The other structural idea worth knowing before you touch this code: **DD/FD (IX/IY) prefixes do
not get their own duplicate opcode tables.** `ExecuteIndexedPrefix` just sets an `IndexMode` flag
and then calls straight back into the same `ExecuteMainOpcode`/`ExecuteMainOpcodeExplicit`
dispatcher used for unprefixed opcodes; every place that would normally read/write `HL`/`(HL)`
transparently redirects through an `IndexBase` property and an `IndexedAddress()` helper to
`IX`/`IY`/`(IX+d)`/`(IY+d)` instead. If you add a new HL-touching opcode, route it through
`IndexBase`, not a literal `HL` reference, or it won't get IX/IY substitution for free.

### Interrupts

Two independent one-shot latches: `NmiPending` and `InterruptPending` (with an accompanying
data-bus byte a real interrupting device would present). Both are "requested once, consumed once
serviced" — there's no device that needs to hold a level asserted. NMI is unconditional and jumps
to `0x0066`; on the Genesis this line isn't actually wired to anything, but it's cheap to support
correctly. The maskable INT is what the Genesis wires the VDP's vblank pulse to, and is what lets
the Z80 sound driver know a new frame started; it only fires if `Iff1` is set, and in **IM1** (the
mode the Genesis sound driver actually uses) jumps to the fixed vector `0x0038`.

### Save state

`Z80.SaveState.cs` — both register sets (main + shadow), IX/IY/SP/PC, I/R, both interrupt
flip-flops, interrupt mode, `Halted`, `TotalCycles`, and both interrupt latches (including the
pending-interrupt data-bus byte, since `NmiPending`/`InterruptPending` can legitimately still be
true at a frame boundary if `Iff1` was disabled when the interrupt was raised). Deliberately
excludes `_indexMode`/`_displacement` — those are only ever valid *mid*-`Step()`, and are always
back to their default between calls (unlike the 68000, a Z80 `Step()` for a DD/FD-prefixed
instruction is not fully atomic from the outside, but no externally-observable state survives
between `Step()` calls, so this is safe).

### Known gaps and quirks

- Undocumented IXH/IXL/IYH/IYL half-register access is not implemented.
- Undocumented X/Y flag bits (3/5) are a best-effort approximation, not bit-exact to silicon for
  every corner case (e.g. `BIT n,(HL)`'s internal-latch behavior).
- DDCB/FDCB's undocumented "also writes back to the named register" quirk is not reproduced (no
  assembler emits that form, so nothing should depend on it).
- DD/FD indexed-prefix timing constants match well-known figures for common cases but haven't been
  cross-checked instruction-by-instruction against silicon.
- Block I/O instruction flags (INI/IND/OUTI/OUTD and their repeated forms) use a documented
  simplification: Z/N are set from the decremented B register (universally agreed), the rest is a
  reasonable approximation rather than the notoriously intricate full rule set.
- R increments once per instruction, not once per M1 cycle (an acknowledged inaccuracy for code
  that reads R via `LD A,R` for entropy/timing tricks).
- Any genuinely unassigned opcode (main table or ED-prefixed) throws rather than behaving as a NOP.

---

## 4a. The SH-2 CPU core (32X, in progress)

`src/GenesisSharp.CpuSh2/`, class `Sh2` — a `public sealed partial class` split across ~13 files
by instruction category (`Sh2.DataTransfer.cs`, `Sh2.Arithmetic.cs`, `Sh2.Logic.cs`,
`Sh2.Shift.cs`, `Sh2.Divide.cs`, `Sh2.MacMultiply.cs`, `Sh2.ControlFlow.cs`,
`Sh2.SystemControl.cs`, `Sh2.Interrupts.cs`, `Sh2.Flags.cs`, `Sh2.Decode.cs`, `Sh2.cs`,
`Sh2.SaveState.cs`). This is **Phase 1 of an in-progress Sega 32X extension** — a standalone,
console-agnostic SH-2 interpreter with no 32X-specific knowledge, exactly as self-contained as
`M68000`/`Z80` are. As of **Phase 2** (§4a.1, below), `GenesisSharp.Core` does construct and step
two instances of it; as of **Phase 3** (§4a.2) a 32X title's own frame buffer graphics can actually
be seen; as of **Phase 4** (§4a.3) its PWM audio can be heard, mixed in alongside PSG/YM2612; and
as of **Phase 5** (§4a.4) the whole subsystem round-trips through save states and has a live debug
tab.

Ground truth for this core is **PicoDrive** (`reference/PicoDrive/picodrive/`), not
genesis-plus-gx — genesis-plus-gx has never shipped 32X/SH-2 support. See §13 for its license and
citation details.

### Bus abstraction

```csharp
public interface IBus
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadLong(uint address);
    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteLong(uint address, uint value);
}
```
Shaped like the 68000's (width-typed methods, no port space — SH-2 has no port I/O), but with a
full 32-bit `address` and no masking assumed at this layer, unlike the 68000's 24-bit bus. Which
address bits are physically wired, and how the CS0–CS3 areas decode, is entirely a 32X-integration
concern for a later phase — this core makes no assumption about it.

### Register file

`uint[16] R`, `PC`, `PR` (subroutine return address — SH-2's equivalent of a link register), `GBR`,
`VBR` (vector base — SH-2's exception vector table location is configurable, unlike the 68000's
fixed table), `MACH`/`MACL` (the 64-bit MAC accumulator, split in two), `SR` (T/S/I3-I0/Q/M bits —
see `Sh2.Flags.cs`), `TotalCycles`.

### Delayed-branch strategy — the key structural idea

SH-2 is a delayed-branch architecture (MIPS/SPARC-style): the instruction immediately following
`BRA`/`BSR`/`BRAF`/`BSRF`/`JMP`/`JSR`/`RTS`/`RTE`/`BT.S`/`BF.S` — the "delay slot" — always executes
*before* the branch takes effect. Plain (non-delayed) `BT`/`BF` do **not** have a delay slot; this
split is the single easiest mistake to make in this core.

Rather than exposing the delay slot as state that survives between `Step()` calls, one `Step()`
call fully consumes a delayed branch *and* its mandatory delay-slot instruction as one atomic unit
(`ExecuteDelayedBranch` in `Sh2.ControlFlow.cs`: fetch the slot opcode, execute it, then apply the
branch's `PC` update, returning their combined cycle cost). This mirrors `M68000`'s existing
"`Step()` is atomic between calls" contract, and gets "interrupts aren't sampled between a branch
and its delay slot" for free, with no special-cased suppression check needed. A nested
`BRA`/`BSR` inside a delay slot is detected and raises the illegal-*slot*-instruction exception
(vector 6, distinct from plain illegal instruction's vector 4) — the one case PicoDrive itself
models for this condition.

### Decode structure

SH-2's fixed 16-bit, no-prefix-byte encoding is regular enough that `Sh2.Decode.cs` follows the
Z80 core's "switch on the opcode's top nibble, then an inner switch/switch-expression on the low
bits" shape rather than the 68000's hand-written tree — mirroring PicoDrive's own
`op0000`–`op1111` dispatch functions one-for-one, including which nibbles get a flat
switch-expression versus a `switch` statement for irregular sub-ranges. Every dispatch arm that
PicoDrive itself decodes as `ILLEGAL` calls `RaiseIllegalInstruction()` (the real hardware
behavior); every other arm implements the real instruction. **The full standard SH-2 opcode set
is implemented** — no on-chip cache, DMAC, timers, or serial are modeled (those are bus/peripheral
concerns for the 32X integration phases, not CPU-core concerns, consistent with how VDP/YM2612/PSG
are already kept separate from `M68000`/`Z80`).

Two categories of instruction were ported *literally* from PicoDrive's C rather than reformulated,
because their carry/overflow bookkeeping is easy to get subtly wrong: `ADDC`/`SUBC` (two separate
overflow checks — the plain add/subtract, then whether the carry-in bit itself overflows — both
needed; an initial single-check version of `ADDC` shipped without a test and was later found wrong
for e.g. `R[n]=0xFFFFFFFF,R[m]=0,T=1`), `ADDV`/`SUBV`/`NEGC` (the dest/src/ans sign-bookkeeping
pattern), `MAC.L`/`MAC.W`'s saturation logic, and `DIV1`'s Q/M/T bit manipulation (a hand
re-derivation of `DIV1` caught one inverted comparison in its four-branch table before it ever
shipped; `Sh2Tests.cs` also cross-checks it against an independently-transcribed reference oracle
across 500 randomized operand/flag combinations). `DMULS`/`DMULU` and the pure-multiply term of
`MAC.L`/`MAC.W` use native 64-bit C# arithmetic instead of PicoDrive's manual 16-bit-partial-product
expansion — provably equivalent, not an approximation.

### Interrupts

`Sh2.Interrupts.cs` — a hybrid of the two existing cores' shapes: a 68000-style priority-mask
`RaiseInterrupt(level, vectorNumber)` (only services if `level` exceeds `SR`'s I3-I0 field, and
raises the mask to the serviced level), plus a Z80-style one-shot `RaiseNonMaskableInterrupt()`
latch that's always taken regardless of the mask — explicitly flagged in-source as **unverified**,
since PicoDrive itself doesn't model SH-2 NMI at all. Vector fetch is VBR-relative
(`PC = bus.ReadLong(VBR + vector*4)`). `LDC Rm,SR`/`LDC.L @Rm+,SR` (which can change the interrupt
mask) don't need any extra "recheck interrupts" bookkeeping the way PicoDrive's own `test_irq` flag
does — `ServicePendingInterrupt()` already re-evaluates against the current `SR` at the top of
every `Step()`, so a changed mask takes effect on the very next `Step()` for free.

### Save state

`Sh2.SaveState.cs` — all 16 `R`, `PC`, `PR`, `GBR`, `VBR`, `MACH`, `MACL`, `SR`, `TotalCycles`,
pending-interrupt level/vector, NMI-pending flag; fixed order, no framing, matching the existing
two cores. No delay-slot-related field is needed, precisely because the `Step()` design above never
leaves that state observable between calls. Wired into `GenesisConsole.SaveState.cs` as of Phase 5
(§4a.4, below).

### Known gaps

- On-chip cache, DMAC, on-chip timers, and serial communication are unimplemented — bus/peripheral
  concerns deferred to the 32X integration phases.
- `RaiseNonMaskableInterrupt()`'s vector number and exact interrupt-entry cost are recalled from
  general SH-2 documentation, not confirmed against PicoDrive (which doesn't model SH-2 NMI).
- Misaligned-address exceptions are unimplemented (matches this codebase's existing acceptance of
  the 68000 core's NEGX/bus-error gap).
- No hardware test-vector suite is known to exist for SH-2 (unlike the 68000 core's
  SingleStepTests), so confidence here rests on PicoDrive source comparison plus this project's own
  unit tests, not independent hardware validation.
- Frontend support (debugger tab, disassembler) landed in Phase 5 — see §4a.4.

### 4a.1 32X bus integration (Phase 2)

`src/GenesisSharp.Core/Sega32X.cs` + `Sega32X.Bus.cs` — the MVP bus-integration layer: both SH-2s
can now be released from reset by the 68000 and exchange data with it through shared communication
registers, verified end-to-end by a synthetic test running real hand-assembled code on both CPUs
(`GenesisConsoleSh2BusIntegrationTests.cs`) rather than a real ROM. Ground truth:
`reference/PicoDrive/picodrive/pico/32x/memory.c` and `32x.c` (no 32X source exists in
genesis-plus-gx) — genesis-plus-gx has never shipped 32X support, so this phase's memory map is
sourced entirely from PicoDrive, cited per-member in `Sega32X.cs`.

`GenesisConsole` now owns a `Sega32X Sega32X { get; }`, constructed unconditionally like
`Vdp`/`Ym2612`/`Psg` — a non-32X ROM simply never writes the ADEN bit, so its two SH-2s are never
stepped in `RunScanline()` and nothing here is observable to it. `Sega32X` holds the whole
$A15100-$A1513F adapter/control register block as one flat `ushort[0x20]` (confirmed against
PicoDrive's own `Pico32x.regs[0x20]`), 256KB of SH-2-side SDRAM, and each core's own boot-ROM
buffer (2KB master / 1KB slave, matching PicoDrive's `sh2_rom_m`/`sh2_rom_s` sizes). Each SH-2 gets
its own `Sh2.IBus` adapter (`Sega32XSh2Bus`) implementing the SH-2's CS0 (boot ROM + adapter
registers)/CS1 (cartridge ROM, unbanked)/CS3 (SDRAM) address decode; CS2 (frame buffer) is left
entirely unmapped this phase. The 68000 side gets two new address predicates in
`GenesisConsole.cs` (`Is32XRegister` for the register block, `IsMarsIdRegister` for the
`$A130EC` "MARS" hardware-presence probe), added to the same `if`-chain `IsVdpPort`/
`IsZ80BusRequestRegister` already use.

**Reset/enable gating**, confirmed against PicoDrive's own condition (`(regs[0] & (nRES|ADEN)) ==
(nRES|ADEN)`, checked before running or interrupting either SH-2): both bits live in the same
control byte, both SH-2s share a single reset line (no independent per-core reset exists in the
register model), and a 0→1 transition of nRES resets both cores together. `RunScanline()` steps
both SH-2s at a per-scanline budget of `CyclesPerScanlineM68000 * 3` (the same textbook ~3× ratio
decided during this project's 32X planning), gated on that same condition, mirroring the Z80 bus
arbitration code's "genuinely stopped while held, not just skipped mid-loop" shape exactly.

**Explicitly deferred this phase** (named, not silently dropped — see `Sega32X.cs`'s own remarks):
the frame buffer/VDP overlay (CS2, the 68k `$840000`/`$860000` windows, `vdp_regs`, palette) — now
built in Phase 3 (§4a.2, below); PWM (Phase 4); DREQ/DMAC (storage-only then — the on-chip DMAC and
PWM's RTP-driven DREQ1 auto-feed have since landed, §4a.3);
interrupt routing (VRES/VINT/HINT/CMD/PWM) — not needed for the register-block wiring this phase
proves; the 68k-side ROM banking window (`$900000-$9FFFFF`) — SH-2 CS1 reads the cartridge image
directly and unbanked instead; a synthesized boot-stub/Initial-Data-Load fallback the way PicoDrive
itself falls back to when no real 32X BIOS is supplied — real, unmodified 32X ROMs won't boot
without this (or a real BIOS, which this project won't ship), so it's deferred to whenever
real-ROM boot is attempted (Phase 6; built in §4a.5, below); and 32X-ROM auto-detection — the
subsystem is always wired in and simply stays inert unless a ROM's own boot code sets ADEN itself.

### 4a.2 32X VDP frame buffer graphics (Phase 3)

`src/GenesisSharp.Core/Sega32X.Vdp.cs` — the 32X's own frame-buffer-based graphics chip: its
double-buffered frame buffer, its 256-entry palette, its display-mode/fill/FBCR register block,
and the compositing rule that overlays its output onto the Genesis VDP's own — all three display
modes (Packed Pixel, Direct Color, Run Length). Ground truth:
`reference/PicoDrive/picodrive/pico/32x/draw.c` and `32x.c`, cited per-member in `Sega32X.Vdp.cs`.

**Run Length** is the one mode whose pixels aren't directly addressable: each frame-buffer word is
a run (low byte = palette index, high byte = length − 1), so a pixel's value depends on every run
before it on that line. Answering per-pixel would be quadratic, so `DecodeRunLengthLine` expands a
whole scanline on first touch and serves the rest from that. The cache is invalidated on `FS`
swap — which is exactly and only when displayed content can change, since both CPUs' frame-buffer
windows always target the *write* bank, so nothing can write the bank currently on screen. A bank
index alone would be insufficient: `FS` going 0→1→0 returns to a bank rewritten in between.
Overlong runs truncate at the line edge, and data running out early pads with index 0. Unlike
Packed Pixel, this mode does not apply `SFT`.

**The rendering hook**: `Vdp` stays 32X-agnostic, exactly like it already is about `Cartridge` —
`Vdp.External32XPixelBlend` (a `delegate bool Try32XPixelBlend(int x, int y, bool
isGenesisBackdrop, bool isH32, out byte r, out byte g, out byte b)`) is wired to
`Sega32X.TryGetPixel` in `GenesisConsole`'s constructor and called from `Vdp.RenderScanline`
right after the existing per-pixel compositing has resolved a Genesis pixel, immediately before
it's committed to `FrameBuffer`. For any non-32X ROM, `TryGetPixel` returns false on its very
first check (display mode off, the power-on default) — a true no-op, same safety shape as Phase
2's always-wired-but-inert register block; the full existing test suite (which already exercises
real non-32X ROMs via `SagaRoms/`) is the regression guard for this claim.

**Compositing rule** — confirmed to be *not* a simple "32X always wins" (`draw.c:62-105`): the 32X
pixel shows unconditionally wherever the Genesis plane resolved to its own backdrop color
(`reg[7]&0x3f`), and otherwise only if its own priority bit (a bit baked into each palette entry
for Packed Pixel, or bit 15 of the raw pixel word for Direct Color — inverted globally by the
`PRI` register bit) is set; otherwise the Genesis pixel wins.

**Frame-buffer addressing is not a fixed stride** — the low 512 bytes of each bank hold a
256-entry table of per-scanline word offsets into the same bank, which real 32X software writes
itself before drawing a frame; rendering scanline `y` reads `dram[y]` to find where that line's
pixel data starts. The index is the scanline number itself, with no offset in either V28 or V30
(`draw.c:206,228,251`: `dram[l + (lines_sft_offs >> 24)]`, where `l` counts visible lines from 0
and the `>> 24` field is `sync_line`, the partial-render resume point — 0 for a whole-frame
render).

This is worth stating explicitly because the obvious-looking candidate is a trap this codebase
fell into twice. `Pico32xRenderSync`'s `offs = 8; if (Pico.video.reg[1] & 8) offs = 0;`
(`32x.c:257-259`) reads exactly like a V28/V30 line-table offset and is nothing of the sort — it
feeds `Pico.est.DrawLineDest = DrawLineDestBase32x + offs * DrawLineDestIncrement32x`
(`draw.c:290`), i.e. it is a *destination* offset centring a 224-line picture inside PicoDrive's
240-line output buffer, and reaches the draw loops only in `lines_sft_offs`'s low byte, which they
never read. GenesisSharp's `Vdp.FrameBuffer` is the 224-line active display alone, so the
equivalent centring offset here is structurally zero. Using `dram[y+8]` instead cost real,
visible corruption on a real title — see `Sega32X.Vdp.cs`'s `TryGetPixel` remarks.

H32 mode additionally offsets the Genesis x-coordinate by 4 relative to the 32X pixel index
(confirmed directly against `draw.c:18-19,148`'s `pmd += H32_OFFSET`).

**Double buffering**: FBCR's `FS` bit selects which bank rendering reads from (`DisplayBankIndex`)
— the SH-2s' CS2 window and the 68000's `$840000`/`$860000` windows always target the *other*
bank (`WriteBankIndex`), so software can draw the next frame without corrupting what's currently
displayed. Writing `FS` applies immediately while blanking, otherwise it's deferred to the next
vblank-start transition, driven by `Sega32X.UpdateBlankingState` — called once per scanline from
`GenesisConsole.RunScanline`, which is also what keeps `VBLK` genuinely mirroring the Genesis
VDP's own vblank window rather than running on an independent clock. `HBLK`/`nFEN` are **not**
real hardware timing — confirmed as an admitted hack even in PicoDrive itself (its own author:
"what's the deal with that?"), faked here identically via a free-running counter incremented on
every FBCR read.

**Known gaps this phase**: real multi-cycle autofill timing (the burst
completes instantly on write); the exact inclusive/exclusive word-count convention for the
autofill length register (implemented as length+1 words, a reasonable but not byte-exact-confirmed
interpretation); which physical R/G/B channel each of the three 5-bit fields in a 5:5:5 color
corresponds to (the source only confirms relative bit positions, never absolute channel identity
— this core labels them R/G/B in low-to-high bit order as its own choice, not a stated hardware
fact); 32X-side VINT/HINT interrupts (unchanged from Phase 2 — polling `FBCR` is enough for MVP
compositing).

### 4a.3 PWM audio (Phase 4)

`src/GenesisSharp.Core/Sega32X.Pwm.cs` — the 32X's PWM sound chip. Despite the name, this is not
real duty-cycle waveform synthesis: PicoDrive's own `convert_sample` treats each FIFO entry as a
linear amplitude value and rescales it directly into a PCM-ish sample
(`v` clamped to the current cycle-register-derived period, then `(v * mult >> 8) - 0x8000`) — this
core does the same. Ground truth: `reference/PicoDrive/picodrive/pico/32x/pwm.c`, cited per-member
in `Sega32X.Pwm.cs`.

**No new bus wiring needed** — PWM's registers (bytes 0x30-0x3f) live inside the same
$A15100-$A1513F/$4000-$403F block Phase 2 already wired end-to-end; the new register side effects
(FIFO push on odd-address write, FULL/EMPTY status-bit reads, the confirmed asymmetric write
permission — the 68000 can only ever affect the routing nibble, RTP and the IRQ-timer field are
SH-2-exclusive) are dispatched from *inside* `Sega32X.cs`'s existing
`WriteControlByteFrom68k`/`WriteRegisterByteFromSh2`/`ReadControlByteFor68k`/
`ReadControlByteForSh2` for offsets ≥ 0x30.

**Per-sample generation, not PicoDrive's batched resampling**: PicoDrive generates PWM into an
internal ring buffer continuously as the SH-2 executes and resamples that buffer once per host
audio callback. `Sega32X.GeneratePwmSample(sampleDurationSeconds)` instead produces exactly one
output sample per call — matching how `Psg`/`Ym2612` already work — by tracking its own
elapsed-cycle debt (in 32x-cycle units, using the *fixed* 3× SH-2:68000 hardware ratio confirmed
distinct from whatever throttled SH-2 execution-speed multiplier drives instruction stepping
elsewhere) and only dequeuing a FIFO entry once that debt crosses the current sample period,
otherwise returning the previously-held value. This reproduces PicoDrive's sample-and-hold
decimation *and* its underrun-holds-last-value behavior with no separate ring buffer needed.
`GenesisConsole.GenerateAudioSample()` mixes its stereo output in alongside PSG/YM2612 — no
`RunScanline` change was needed, since audio generation already runs at the right cadence.

**PWM's two "feed me more data" mechanisms are both implemented.** The interrupt (`P32XI_PWM`,
§4a.6) is one; the other is **RTP**, control-register bit 7, which makes each PWM interrupt also
assert the chip's DMA request line so a DMAC channel can refill the FIFO without the SH-2 writing
each sample by hand. `Sega32XSh2Bus.TriggerDreq1` answers it, and differs from an auto-request
transfer in three ways that all matter: it moves **exactly one unit per request** (PicoDrive's
`dmac_transfer_one`, not a loop — draining the buffer in one go would overrun the 3-entry FIFO
immediately), it is channel 1 only, and it deliberately does *not* require `AR`, since this
**is** the external request an `AR`-clear channel is waiting for. Completion sets `TE` and, if
`IE` is set, raises the SH-2's own DMA-complete interrupt through the internal (auto-clearing)
path — not the external IRL pins, matching PicoDrive's `sh2_internal_irq` in `dmac_te_irq`.

**Known gaps this phase**: the exact bit-field meaning of routing-nibble values other than the two confirmed stereo modes
(normal/swapped) and the four confirmed-invalid ones — treated here as mono — isn't fully resolved
even in PicoDrive's own source.

### 4a.4 Frontend integration and save states (Phase 5)

Unlike Phases 2-4, this phase is about integrating with GenesisSharp's own existing frontend/
save-state code, not more PicoDrive hardware research — ground truth here is this codebase's own
established conventions (`Vdp.SaveState.cs`'s array-persistence idiom, `M68kDisassembler`/
`Z80Disassembler`'s shape, `DebugForm`'s existing tab layout), cited per-section below.

**Save states** — `src/GenesisSharp.Core/Sega32X.SaveState.cs`: `SaveState`/`LoadState` covering
the entire subsystem — the adapter/control register block, SDRAM, both boot ROMs (currently always
blank during normal play; included anyway so a future boot-stub-synthesis phase doesn't silently
need another version bump to start persisting them), both SH-2 cores' own state (delegating to
`Sh2.SaveState`/`Sh2.LoadState`), the VDP overlay's registers/palette/frame buffers/blanking
bookkeeping, and the PWM chip's FIFOs/held samples/cycle debt. Wired into
`GenesisConsole.SaveState.cs` right after `ExtPort`, alongside two new raw fields
(`_msh2CycleDebt`/`_ssh2CycleDebt`, the SH-2 equivalents of the existing `_z80CycleDebt`) —
`SaveStateVersion` bumped 1→2. Verified by `Sega32XSaveStateTests.cs` (5 tests): each state
category round-trips independently (both SH-2 cores' registers, the adapter/COMM registers, the
VDP overlay, the PWM FIFO), plus an end-to-end test that loads a save state into a *fresh*
`GenesisConsole` and confirms 10 more frames of both video and audio output stay bit-identical to
the original.

**Disassembler** — `src/GenesisSharp.Frontend/Sh2Disassembler.cs`: mirrors `M68kDisassembler`/
`Z80Disassembler`'s exact shape (`Decode`/`DisassembleRange`/hardware-register trailing-comment
annotation, wrapped in the same try/catch-falls-back-to-a-raw-hex-dump pattern), reusing the
existing `DisassembledInstruction` record and `DisassemblyLabeler`. Built directly from
`Sh2.Decode.cs`'s own dispatch tables and each instruction's implementation in the `Sh2.*.cs`
files — a from-scratch encoder of the *same* opcode map the core itself decodes, not an
independent transcription of the SH-2 ISA, so it can't drift from what the core actually executes.
Displacement operands are shown pre-scaled to bytes (e.g. `MOV.L R3,@(20,R2)` for a disp4 of 5),
matching the official Hitachi/Renesas assembly mnemonic convention rather than the raw encoded
nibble. PC-relative loads (`MOV.W`/`MOV.L @(disp,PC)`, `MOVA`) resolve to a concrete target address
using the same "PC as it stands right after this instruction's own fetch, plus 2" convention
`Sh2.DataTransfer.cs` itself uses — the address is statically known at disassembly time even though
the loaded *value* isn't. Every opcode `Sh2.Decode.cs`'s own dispatch tables treat as
architecturally illegal is reported as literal `ILLEGAL` text (a known fact, not a gap); a genuine
decode-time exception falls back to a `.WORD` hex dump. No automated test file exists for it,
matching the existing precedent that neither `M68kDisassembler` nor `Z80Disassembler` has one
either — instead it was verified with a throwaway scratch harness (not checked in) exercising
~30 hand-encoded opcodes spanning every dispatch group, including branch-target arithmetic and the
hardware-register annotation.

**Debug window** — `DebugForm.cs` gained a "32X" tab, same registers-box-on-top/two-column-
disassembly-below layout as the existing CPU tab: live register dumps for both `Sega32X.MasterSh2`/
`SlaveSh2` (all 16 `R`, PC, SR, PR, GBR, VBR, MACH, MACL, cycle count) plus the adapter's nRES/ADEN
release state, and a labeled disassembly listing for each core starting at its current PC. Peek
reads for disassembly go through two new `Sega32X.MasterSh2Bus`/`SlaveSh2Bus` properties — the same
`Sega32XSh2Bus` instance each core already executes against, exposed as the public `CpuSh2.IBus`
interface type for exactly this read-only purpose (the concrete bus class itself stays `internal`,
the same pattern `GenesisConsole` itself uses for the 68000/Z80 disassemblers).

**32X ROM indicator** — `Cartridge.Is32X`: a minimal, display-only check of the standard Genesis
header's console-name field (16 ASCII bytes at ROM offset 0x100), true if it contains `"SEGA 32X"`
— the documented convention real 32X titles use to identify themselves to the 32X's own boot ROM.
`MainForm` appends `" [32X]"` to the window title when set. Purely cosmetic: nothing in
`GenesisConsole`/`Sega32X` reads this property — the 32X subsystem is always wired in and is only
ever activated by a ROM's own code setting ADEN/nRES itself, exactly as it was before this
property existed. This is also the first header-parsing code anywhere in `Cartridge` — everything
else about cartridge loading remains the same "raw headerless image" scaffolding described at the
top of this section.

**Known gaps this phase**: none within Phase 5's own defined scope — Phase 6 (validation against
real, legally-owned 32X ROM dumps) is the only phase left in the 32X plan.

### 4a.5 Synthesized boot stub (Phase 6, in progress)

Loading a real, unmodified 32X ROM against everything through Phase 5 hung on a perpetual illegal-
instruction trap: both SH-2s reset to `PC=0`, which is inside `BootRomMaster`/`BootRomSlave` — and
those start out blank, since GenesisSharp has no real 32X BIOS to put there (Sega's own firmware,
not something this project can ship). Opcode `0x0000` decodes as illegal, the illegal-instruction
handler's own vector (also blank) points right back at `0`, and the core spins forever. This is
exactly the gap every earlier phase named explicitly ("no synthesized boot-stub fallback").

`src/GenesisSharp.Core/Sega32X.BootStub.cs` fixes this the same way PicoDrive itself does when no
real BIOS is supplied — ground truth: PicoDrive's `get_bios()`
(`reference/PicoDrive/picodrive/pico/32x/memory.c:2195-2294`) for boot-ROM *content*, and
`p32x_reset_sh2s` (`reference/PicoDrive/picodrive/pico/32x/32x.c:161-209`) for host-side setup —
both already a synthesized fallback in PicoDrive itself, not a real Sega BIOS dump; re-encoded here
as equivalent C#/SH-2 machine code rather than copied from PicoDrive's C source.

- **Boot-ROM content**, populated once at `Sega32X` construction (not on every reset — a real boot
  ROM is fixed silicon, and Phase 2's own tests, which inject their own hand-assembled program into
  these same public arrays, run *after* construction and simply overwrite this default): every
  exception vector defaults to a trap-to-self, except the reset vector, which points at a small
  stub ported opcode-for-opcode from PicoDrive's `msh2_code`/`ssh2_code`. The master's stub writes
  `"M_OK"` into COMM0:COMM1, then jumps to the entry point named at cartridge ROM offset `$3E0`; the
  slave's stub *waits* for that `"M_OK"` (the real cross-CPU synchronization point), writes
  `"S_OK"` into COMM2:COMM3, and jumps to the entry point at `$3E4` — both offsets are the
  documented 32X ROM header convention, not something specific to PicoDrive.
- **Host-side setup**, run every time nRES is asserted (both at power-on via `Reset()` and at any
  later 68000-triggered nRES edge, matching PicoDrive's own call sites for `p32x_reset_sh2s`
  exactly): each core's `GBR` is set to the adapter-register base (so the stub's GBR-relative COMM
  reads/writes actually land there instead of on the boot ROM itself), each core's `VBR` is set from
  the header (`$3E8`/`$3EC`), and the header-named "Initial Data Load" block is copied from
  cartridge ROM into SDRAM (`$3D4`/`$3D8`/`$3DC` name the source/destination/size) before either
  core starts running.
- **A confirmed Phase 2 bug fixed as part of this work**: the SH-2-visible "no cartridge" bit
  (`NCartBit`) was being OR'd in as *always set* — backwards. PicoDrive's own naming (`ncart_in`,
  documented in its own source as `"!cart_in"`) confirms the "n" prefix is genuinely active-low:
  the bit means *no* cartridge, and GenesisSharp — which always has one — must never set it. With
  the bug, both SH-2 boot stubs would have taken their dead-end Sega-CD `"_CD_"`-wait branch instead
  of ever reaching real game code, regardless of anything else in this file being correct.
- **Verification**: `Sega32XBootStubTests.cs` builds a ROM with nothing but the header fields above
  plus two tiny hand-assembled SH-2 programs at the named entry points, releases nRES/ADEN through
  the same public 68000-side bus a real console would use (no test-injected boot code, unlike Phase
  2's own coverage), and confirms both cores actually reach and execute their entry-point code —
  the same path a real ROM's own boot sequence exercises.

**Four more real-ROM-only bugs found and fixed testing this against an actual commercial 32X
title** (user-supplied, legally-owned dump — the first real-ROM validation this project has had):

- **The 68000-side ROM banking window (`$900000-$9FFFFF`) was entirely unmapped.** Real 32X boot
  code reads through here (a selectable 1MB slice of the same cartridge ROM already visible at
  `$000000-$3FFFFF`, bank chosen by the adapter register block's byte offset 5 — confirmed against
  PicoDrive's `bank_switch_rom_68k`/`case 0x05: // bank`, `memory.c:439-444,1449-1484`) to reach ROM
  content beyond whatever the base cartridge window exposes. Phase 2 explicitly deferred this (its
  own synthetic test never needed it); a real title's own boot sequence does. With it unmapped, a
  jump in here read open bus (`$FFFF`, an illegal 68000 opcode) and got stuck — directly observable
  in the debug window as the 68000's own PC cycling through a small illegal-instruction address
  range inside that window. `Sega32X.ReadRomBankWindowByte` (new) computes the bank-selected byte
  straight out of the already-fully-resident cartridge image, no separate bank-switch step needed —
  the same "no chunking" reasoning `CartridgeRom`'s own remarks already give for the SH-2 side.
  Writes through this window are dropped, matching plain cartridge ROM's existing read-only wiring.
- **SH-2 starvation from `RunScanline`'s once-per-scanline release check.** Even with the window
  above mapped, a real title's own boot-handshake polling loop could still toggle nRES/ADEN off
  again within the same scanline's ~488-cycle 68000 budget — and `RunScanline` only checked
  `Sega32X.Sh2sReleased` once, *after* the 68000 had already run that whole budget. If the release
  condition was false again by that one checkpoint (which a fast-enough retry loop hits on *every*
  scanline), both SH-2s got zero cycles, forever — directly observable as the SH-2's own PC frozen
  exactly on its reset vector while the 68000's PC kept moving. Fixed by checking
  `Sh2sReleased`and stepping both SH-2s once per 68000 *instruction* rather than once per scanline
  (proportional to that instruction's own cycle cost, preserving the same aggregate ~3x-of-68000
  per-scanline SH-2 budget in total — see `RunScanline`'s own remarks). This is a change to the
  core scanline loop every ROM runs through, 32X or not; the existing non-32X regression suite
  (unaffected, since `Sh2sReleased` is unconditionally false for any ROM that never touches the
  32X registers) is the safety net for that.
- **`WriteControlByteFrom68k` did a naive full-byte overwrite of the nRES/ADEN byte ($A15101),
  clobbering `REN` (bit 7) on every write.** Real 32X boot code polls `REN` — some titles wait for
  it to read back set before proceeding — and on real hardware it's essentially independent of
  nRES/ADEN, surviving writes to this byte untouched. GenesisSharp's old plain-storage write meant
  any ordinary `MOVE.B #$03,($A15101)` to release the SH-2s also silently zeroed `REN` in the same
  operation, hanging that poll forever — directly observable as the 68000 settling into a new small
  loop around a `BTST #7,(...)` instruction once the two fixes above got it that far. Confirmed
  against PicoDrive's own `p32x_reg_write8` (`memory.c:406-426`, its own comment: "writable bits
  tested"): only `nRES`/`ADEN` (bits 1/0) are ever actually stored from a write to this byte, and
  offset 0 is masked the same way (only `FM`, bit 7, is 68000-writable there). PicoDrive's handler
  also forces `nRES` back to released whenever `ADEN` transitions 1→0 (a real subsystem shutdown
  leaves the SH-2s not-held but the whole subsystem inert, the same shape as the power-on default)
  — reproduced here too, not just the masking.
- **`FM` is writable from the *SH-2* side too, and dropping that write disarms everything else
  that core does to the 32X VDP.** Adapter-block offset 0 is not 68000-exclusive: an SH-2 writes
  `FM` itself to take ownership of the VDP register block, the palette, and the frame buffer before
  touching any of them. Confirmed against PicoDrive's `p32x_sh2reg_write8` (`memory.c`, `case 0x00:
  // FM`: `r[0] &= ~P32XS_FM; r[0] |= (d << 8) & P32XS_FM;`) — only `FM` is merged in, the rest of
  the word stays 68000-owned. `Sega32X.WriteRegisterByteFromSh2` originally discarded offset 0
  outright, so `FM` never left 0 and the (correct, PicoDrive-matching) ownership gate in
  `WriteVdpControlByteFromSh2` then silently swallowed every VDP register write that core made.
  Cost, confirmed on a real title (Pitfall: The Mayan Adventure): its master SH-2 sets `FM`, flips
  FBCR's `FS` to aim the next draw at the other frame-buffer bank, then writes that bank's line
  table. With the `FS` write swallowed, both of the game's two line-table passes landed in the same
  bank; the other bank kept an all-zero line table forever while still receiving pixel data, and
  since the game flips `FS` every frame the display alternated between the finished picture and a
  bank that could not resolve a single scanline — a hard 30Hz flicker. Worth noting as a shape, not
  just an incident: a dropped *ownership* write is silent at the point of failure and only shows up
  much later as unrelated-looking writes going missing.
- **A second, separate 68000-side ROM window (`$880000-$8FFFFF`) was also entirely unmapped.**
  Distinct from the banked window above: this one always shows cartridge ROM starting from offset
  0, completely ignoring the bank-select register — real boot code uses it specifically because
  it's a *stable* way to reach a fixed ROM offset regardless of whatever the $900000 window
  currently has selected. With all three of the fixes above in place, this title's own code was
  traced (live, via a new debug-window address-peek feature — see §9) all the way to setting
  `ADEN=1` and jumping straight into this window — which, unmapped, read open bus and sent the
  whole boot sequence back to the start, repeatedly re-clearing VRAM/CRAM/VSRAM forever without
  ever actually releasing the SH-2s. Confirmed against PicoDrive's own `PicoMemSetup32x` ("32X ROM
  (unbanked...)", `memory.c:2367-2372`), which maps `Pico.rom` here directly with no bank offset
  at all. `Sega32X.ReadRomMirrorWindowByte` (new) mirrors `ReadRomBankWindowByte`'s shape exactly,
  just without the bank computation.
- **Verification**: `Sega32XBootStubTests.cs` covers both ROM windows directly (bank-select writes
  change what the banked window reads back as without affecting the mirror window at all; writes
  through either window are dropped, matching plain ROM), the starvation fix with an adversarial
  68000 program that toggles nRES/ADEN on-then-off in a tight loop entirely within one scanline
  (confirming a test-injected SH-2 program still gets to run despite never seeing a *stable*
  release), and the masked-write fix (`REN` survives writes that set/clear nRES/ADEN; clearing
  `ADEN` forces `NRes` back to `true`).

**Known gaps**: Real-ROM validation (Phase 6's stated goal) is in progress but not complete. One
commercial title now renders and sounds correct end-to-end through its intro and cutscenes, but
that is one title and a few minutes of it; further real-ROM issues may still surface.

### 4a.6 Interrupt routing (VRES/VINT/HINT/CMD/PWM)

`Sega32X.Interrupts.cs`. All five 32X interrupt sources are **SH-2-side only** — nothing in
PicoDrive's 32X code raises an interrupt on the 68000 from a 32X event, confirmed by an exhaustive
search of `32x.c`/`memory.c`/`pwm.c`. The 68000 learns about SH-2 activity by polling COMM
registers.

Levels and vectors, confirmed by hand-tracing `p32x_update_irls`'s priority encoder
(`32x.c:34-74`) against `pico_int.h:624-628`, cross-checked against `sh2_irq_cb`'s auto-vector
formula (`32x.c:18-31`, `return 64 + pending_irl/2` — the SH-2 hardware convention for
auto-vectored external interrupts):

| Source | Pending bit | Level | Vector | Gated by `Sh2IrqMask`? |
| --- | --- | --- | --- | --- |
| VRES | `0x80` | 14 | 71 | **No** — never maskable |
| VINT | `0x40` | 12 | 70 | bit 3 |
| HINT | `0x20` | 10 | 69 | bit 2 |
| CMD | `0x10` | 8 | 68 | bit 1 |
| PWM | `0x08` | 6 | 67 | bit 0 |

**These five are level-triggered.** `Sega32X.Sh2IrqPending[core]` is a bitmask of which sources are
currently *asserting* (PicoDrive's own `Pico32x.sh2irqi[2]`, `pico_int.h:651`), and
`UpdateInterruptRequestLevels` — the equivalent of `p32x_update_irls` (`32x.c:34-74`) — encodes the
highest set bit onto that core's interrupt-request pins via `Sh2.SetInterruptRequestLevel`. Both
derivations fall out of the bit positions: level is `2 × bitIndex`, vector is `64 + bitIndex`, the
latter being exactly PicoDrive's `64 + pending_irl / 2`.

Servicing an interrupt **does not clear it**. The handler must tell the source to stop asserting,
by writing this core's own interrupt-clear register (offsets `0x14`/`0x16`/`0x18`/`0x1c` for
VRES/VINT/HINT/PWM; CMD instead clears the 68000's request bit at `0x1a` and lets its live AND fall
false). Otherwise it legitimately fires again as soon as `RTE` restores SR — real behavior, not a
bug to design around. Re-entry *during* the handler is prevented separately, by SR's I3-I0 mask
being raised to the serviced level. Confirmed against PicoDrive's `sh2_irq_cb` (`32x.c:18-31`),
which clears only on its *internal* path (`sh2->pending_int_irq = 0; // auto-clear`) while the IRL
path returns a vector and clears nothing.

That split is mirrored in the CPU core, which has two separate inputs: `SetInterruptRequestLevel`
for the external pins (level, never auto-clears) and `RaiseInternalInterrupt` for on-chip
peripherals like the SCI and DMAC (one-shot, auto-clears when taken, and carries the peripheral's
own vector rather than a level-derived one). Higher level wins, matching PicoDrive's
`pending_irl > pending_int_irq`.

An earlier revision instead had the CPU keep a *single* pending `(level, vector)` pair, replaced
only by a higher-priority request — which silently **dropped** a lower-priority source asserted
while a higher one was pending, rather than leaving it asserted underneath. That became reachable
as soon as VRES (level 14) started being raised at all.

`Sh2IrqMask` is the SH-2's own view of **adapter-block offset 1**, and this is the subsystem's
sharpest trap: the *same byte offset* means `nRES`/`ADEN` when the **68000** writes it (`$A15101`)
and a per-core interrupt-enable register when an **SH-2** writes it (`$4001`). Not a rename of the
same bits — a genuinely different register depending on who is asking (`memory.c:824-839`).

**VRES is not the SH-2 reset line**, despite the name, and the two are separate mechanisms on real
hardware. `p32x_reset_sh2s` (`32x.c:161-212`), tied to the software `nRES` 0→1 edge, raises no
interrupt anywhere in its body; VRES-the-interrupt comes solely from `PicoReset32x`
(`32x.c:240-249`), the whole-system reset path. So `Sega32X.Reset()` raises it and the `nRES` edge
deliberately does not. It is also applied to both cores *outside* the masked expression the other
four go through (`32x.c:79-88`), so no mask can suppress it. Ordering matters at the call site:
`Sh2.Reset()` clears `PendingInterruptLevel`, so `Sega32X.Reset()` raises VRES last, after both
cores are reset.

CMD is a **live AND** of the 68000's request bit and the target core's mask, re-evaluated whenever
either changes — not a one-shot latch. HINT is driven by the SH-2-exclusive "H count" register
(adapter offset 5, again a different register from the 68000's offset-5 ROM-bank select), counted
down per scanline rather than with PicoDrive's cycle-precise event scheduler — PicoDrive's own
source calls its HINT handling "rather rough... useless in practice" (`32x.c:350`). PWM's counter
is shared across both channels, decremented once per consumed sample period.

**Consequence worth knowing when writing 32X test programs**: because the sources are
level-triggered, a program that unmasks SR without first acknowledging the VRES left asserted by
the whole-system reset immediately takes a VRES it usually has no handler installed for — and then
takes it again, forever. Real boot code acknowledges first; so do the hand-assembled SH-2 programs
in `Sega32XInterruptTests`, via `WriteSh2VResAcknowledge`, with `LDC SR` deliberately last. The
same reason has the test helpers there and in `Sega32XPwmTests` write offset `0x14` after
`Sega32X.Reset()` rather than testing straight off it.

This is also a small, real piece of evidence that the model is right: after the change, Pitfall's
32X setup completes one frame later than before, because its boot code now genuinely services and
acknowledges the power-on VRES first. A ROM that didn't acknowledge would have hung instead.

**Still simplified**: `nRES`-edge core resets re-drive the pins from `Sh2IrqPending` rather than
modelling the reset as an independent input, and interrupt latency is instruction-granular (no
cycle-accurate delivery timing) — the same later-refinement stance every other 32X subsystem here
takes where PicoDrive itself admits uncertainty.

---

## 5. GenesisConsole — the system bus and orchestrator

`src/GenesisSharp.Core/GenesisConsole.cs` (+ `GenesisConsole.SaveState.cs`). This is the class to
read first if you're new to the codebase — it's the motherboard.

### 5.1 What it owns

```csharp
public sealed partial class GenesisConsole : Cpu68000.IBus, CpuZ80.IBus
{
    public Cartridge Cartridge { get; }     // injected via constructor
    public M68000 Cpu { get; }              // main CPU
    public Z80 SoundCpu { get; }            // sound CPU
    public Vdp Vdp { get; }
    public Ym2612 Ym2612 { get; } = new();
    public Psg Psg { get; } = new();
    public ControllerPort ControllerPort1 { get; } = new();
    public ControllerPort ControllerPort2 { get; } = new();
    public ControllerPort ExtPort { get; } = new();
    // + private _workRam (64KB), _z80Ram (8KB), and bus-arbitration state
}
```
It implements **both** CPU cores' `IBus` interfaces itself, and hands `this` to both `new M68000(this)`
and `new Z80(this)` in its constructor. This is the single most important fact about the codebase's
shape: there is one class deciding what every address means, for both CPUs.

### 5.2 The 68000 memory map

Every access masks the address to 24 bits first ("real hardware only has 24 address pins"), then
decodes in this order:

| Address range | Decodes to |
|---|---|
| `0xC00000`–`0xC0001F` | VDP data/control/HV-counter ports |
| `0xA11100`/`0xA11101` | Z80 bus-request register |
| `0xA11200`/`0xA11201` | Z80 reset register |
| `0xA00000`–`0xA0FFFF` | Z80's own address space (only reachable while the bus is granted) |
| `0xA10000`–`0xA1001F` | Version register + controller port data/direction registers |
| `0xA14000`–`0xA14003` | TMSS register (a plain latch — the "SEGA" lock isn't enforced; the VDP always responds) |
| `< 0x400000` | Cartridge ROM, mirrored via `address % Cartridge.Rom.Length`; writes here are silently dropped |
| `>= 0xE00000` | 64KB work RAM, mirrored across the entire `$E00000–$FFFFFF` range |
| anything else | Open bus: reads return `0xFF`, writes are no-ops |

A **separate**, narrower map (`ReadExternalMemoryByte`) exists only for the VDP's DMA-source reads:
ROM plus work RAM aliased at both `0xFF0000+` and `0x7F0000–0x7FFFFF` (DMA source registers are
only 23 bits wide and can't encode RAM's bit 23, so real game code strips it before programming a
DMA — e.g. `$FF8BAC → $7F8BAC`).

**The prefetch-noise quirk** (`0xA11100`/`0xA11101`, bus-request register reads): real hardware
only drives bit 0 (byte) / bit 8 (word) here — the rest of the byte/word reflects whatever the
68000 currently has prefetched at its own PC, an undocumented open-bus behavior confirmed against
genesis-plus-gx's `ctrl_io_read_byte` ("Unused bits return prefetched bus data (Time Killers)").
This was fixed after **Shadow of the Beast** was found to poll this register with `TST.B` instead
of `BTST #0`; returning a clean `0x00`/`0x01` made its busreq-acknowledge wait loop spin forever,
because real prefetch noise in the upper bits almost never happens to also read as zero.

### 5.3 The Z80 memory map and cross-CPU bus arbitration

| Address range | Decodes to |
|---|---|
| `< 0x4000` | Z80's own 8KB RAM, mirrored |
| `0x4000`–`0x5FFF` | YM2612 (write: address/data latch protocol; read: always chip status — registers are write-only) |
| `0x6000`–`0x6FFF` | Bank register (write-only, 9 bits shifted in one bit per write, LSB-first) |
| `0x7000`–`0x7EFF` | unused, open bus |
| `0x7F00`–`0x7FFF` | PSG (write-only) |
| `0x8000`–`0xFFFF` | Banked 32KB window into **68000** address space (base = bank register << 15), routed back through `GenesisConsole`'s own 68000-side `IBus` |

**Bus arbitration**: writing bit 0/8 of `0xA11100`/`0xA11101` sets `_z80BusRequested`; while true,
the Z80 core doesn't step and its RAM becomes directly reachable from the 68000 side. No
arbitration delay is modeled — the grant is immediate, so the classic
`move.w #$100,($A11100); btst #0,($A11101); bne ...` poll idiom exits on its first check. Writing
`0xA11200`/`0xA11201` controls reset: bit clear = held in reset (calls `SoundCpu.Reset()` exactly
on the transition into "held", matching the real boot sequence of request-bus → hold-reset →
load driver into Z80 RAM through the window → release-reset → release-bus). A recursion guard
(`_crossBusAccessInProgress`) prevents a bank register value that points the Z80's window back at
its own RAM region from recursing forever — one round-trip is allowed each direction, then further
access reads as open bus.

### 5.4 The main frame loop

```
RunFrame()
  for each of 262 scanlines (NTSC):
    RunScanline()

RunScanline()
  1. if within the visible 224 lines: Vdp.RenderScanline(currentLine)
  2. 68000: accrue CyclesPerScanlineM68000 (488) into a debt; while debt > 0:
       update Vdp.ScanlineProgress, advance VDP's external slot clock,
       Cpu.Step() (subtract returned cycles), subtract Vdp.ConsumeStallCycles()
       (a DMA/FIFO stall genuinely freezes the CPU here, same as real bus contention)
  3. Z80: same shape with its own cycle budget — but ONLY if the bus isn't
       requested/held; while held, no debt accrues to "catch up" later
  4. Ym2612.AdvanceTimers(...) unconditionally — its clock runs even while
       the Z80 bus is arbitrated away, since it's a separate chip
  5. accrue a fractional "samples worth of time" debt; while >= 1 sample,
       generate one audio sample and enqueue it (dropping the oldest if the
       queue is full — see §5.6)
  6. Vdp.AdvanceScanline() — fires H-interrupt/V-blank events, wraps at 262
```
Any leftover fractional debt in any of these carries forward into the next scanline rather than
being discarded, which is what keeps the long-run cycle/sample rate accurate.

### 5.5 Interrupt delivery

- **VBlank**: the VDP fires `VerticalBlankStarted`, which the console turns into `Cpu.RaiseInterrupt(6)`
  (68000, level 6, autovector `0x78`) **and** `SoundCpu.RaiseMaskableInterrupt()` (Z80's single INT
  line, IM1 → `0x0038`). These two levels were found to need to differ — a homebrew ROM (Crazy
  Driver) hung polling a counter only its level-6 handler incremented, back when the code used to
  request level 4 for both.
- **H-interrupt**: fires every `(register 10) + 1` active scanlines when enabled, reaching only the
  68000 (level 4, autovector `0x70`) — raster-line effects have no meaning to the sound driver.

### 5.6 Audio buffering

Samples are produced by `GenerateAudioSample()` (mixes `Psg.GenerateSample` — mono, duplicated to
both channels — with `Ym2612.GenerateSample` — native stereo — clipping to `short` range) and
pushed into a `ConcurrentQueue<(short Left, short Right)>`, because the emulation thread produces
into it while a separate audio-callback thread drains it. The queue is capped tightly (~60ms of
audio, 3× the frontend's WASAPI latency) — a looser cap previously let the emulation clock (paced
by a `Stopwatch`) and the audio-hardware clock (an independent clock) drift apart until it became
audible lag; the cap trims that drift instead of letting it accumulate. Frontends must pull audio
via `TryDequeueBufferedAudioSample(out left, out right)` — returns `false` (silence) and increments
`AudioUnderrunCount` on an empty queue — and must **never** call `GenerateAudioSample()` directly,
since that would double-advance the chips' oscillator phase.

### 5.7 Cartridge, controllers, TMSS

- `Cartridge.LoadFromBin(byte[] rom)` is deliberately minimal — a raw byte array wrapper. No header
  parsing, no bank-switching mappers (e.g. SSF2), no interleaved `.smd` format support. This is
  explicitly scaffolding for a later milestone (§11 has pointers on where to extend this).
- `ControllerPort`/`GamePad` implement the real TH-multiplexed 3-button protocol plus the 6-button
  pad's extended TH-transition-counting detection sequence, including the hardware timeout
  (`SequenceTimeoutCycles`, ~1.5ms) that lets a normal slow D-pad poll never accidentally trip the
  extended steps while a genuine rapid-fire read routine sails through all six. The exact timeout
  value is the one detail in this file the original implementer flagged as only moderately
  confident — the step sequence itself is solidly documented.
- The TMSS register at `0xA14000` is a plain read/write latch with no gating effect — real hardware
  requires writing "SEGA" there before the VDP responds; this VDP is always active regardless.

---

## 6. The VDP (Video Display Processor)

`src/GenesisSharp.Core/Vdp*.cs` — 14 partial-class files. This is the most complex subsystem in
the codebase, and also the one with the widest confidence spread: some of it (the H-scroll
addressing formula, the FIFO/DMA slot-timing tables, the status register bit layout) has been
directly cross-checked against genesis-plus-gx or BlastEm source, or corrected by real-ROM
regressions; some of it (Mode 4, interlace doubling, the exact shadow/highlight brightness math,
the H-counter's dot-exact jump behavior) is an honest best-effort reconstruction that hasn't had
the same scrutiny. The type-level doc comment on `Vdp.cs` says this explicitly — read it before
assuming any specific bit position is gospel.

This emulator models **Mode 5 (the Genesis's native mode), H40 (320-wide), non-interlaced
rendering** as the well-tested path; Mode 4 and interlace *tracking* exist but see far less
real-world exercise.

### 6.1 Core state (`Vdp.cs`)

- `Vram`: `byte[0x10000]` (64KB)
- `Cram`: `ushort[64]` — one 9-bit color word per entry, 4 palette lines × 16 colors
- `Vsram`: `ushort[40]` — one vertical-scroll value per 2-column pair
- `Registers`: `byte[24]` — the 24 raw VDP registers
- `FrameBuffer`: `byte[320*224*3]`, RGB24, fixed size regardless of H32/H40 (H32 just blanks the right 64 columns)
- Two events: `VerticalBlankStarted`, `HorizontalInterruptRequested` — real hardware shares one
  level-4 68000 autovector between the two; there's no separate H-interrupt vector

### 6.2 Registers (`Vdp.Registers.cs`)

Typed accessors decode the raw register bytes — `DisplayEnabled`, `VerticalInterruptEnabled`,
`DmaEnabled`, `Mode5Enabled`, `Mode4Enabled`, the four plane/window/sprite name-table base
addresses, `HScrollTableBase`, `BackgroundPaletteLine`/`BackgroundColorIndex`, `Is40CellMode`
(H32 vs H40), `ActiveWidth`, `AutoIncrement`, the window split-position registers, plane
width/height (32/64/128 tiles), and the three DMA registers (`DmaLength`, `DmaSourceAddress`,
`CurrentDmaMode`). If you need to add a new register-derived behavior, this is the file to extend
— add a typed accessor here rather than reading `Registers[n]` inline elsewhere.

One correction worth knowing about if you're reading old commit history or discussing this file:
`Mode5Enabled` was originally implemented against the wrong bit (register 1 bit 3, which is
actually the 30-cell/240-line select bit) — a homebrew ROM's very first register-1 write exposed
it, because every existing test had unknowingly been written to reinforce the wrong bit too.

### 6.3 Control/data port protocol (`Vdp.Ports.cs`)

The two-word command sequence, register writes, and DMA triggering all live here. **Two
real-ROM-found bugs were fixed in this exact area** and are worth knowing about if you're touching
this file:
- The second command word's CD5–CD2 bits were originally read from the wrong bit positions (two
  bits too low), which meant the DMA-trigger check sometimes missed a real DMA request entirely.
  Fixed and confirmed against the well-known `VSRAM_ADDR_CMD` constant (`$40000010`).
- VRAM writes are big-endian byte pairs; CRAM/VSRAM index by `(address >> 1) % length`.

### 6.4 DMA and FIFO (`Vdp.Dma.cs`, `Vdp.Fifo.cs`)

Three DMA modes: 68k-memory-to-VRAM, VRAM fill (triggered by the control word, but the fill itself
runs on the *next* data-port write, which supplies the fill byte), and VRAM-to-VRAM copy.
**VRAM-copy DMA charges 2 access-slots per byte, not 1** — copy DMA is a read *and* a write, and
undercharging it by 2× (an earlier bug) was found by comparing against genesis-plus-gx's
`vdp_ctrl.c`, which documents exactly this ("DMA Copy: one read + one write = 2 access"). The FIFO
is modeled as depth-4 (confirmed against **BlastEm**'s `vdp.c` — one of the few emulators accurate
enough to pass Nemesis's VDP FIFO test ROM) with a per-line access-slot budget table
(`SlotsPerLineTable[blanking, H40]`) confirmed against genesis-plus-gx's own `dma_timing[2][2]`.
The VDP's DMA-busy status bit (read via the status register) has a real history worth knowing:
it went from hardcoded-0 → hardcoded-1 → an actually-tracked busy-until-cycle window, driven by two
separate real-ROM regressions (Scorpion Illuminati, Omega Blast) — and matters in practice because
**SGDK's `VDP_waitDMACompletion` polls exactly this bit**, so any homebrew built with SGDK depends
on it being right.

### 6.5 Timing (`Vdp.Timing.cs`, `Vdp.HvCounter.cs`)

Per-scanline H-interrupt countdown, VBlank/HBlank flagging, and the status register (read via the
control port) live here. **The status register's bit layout was rebuilt from a "badly-scrambled
guess" after two separate real-ROM regressions** (Scorpion Illuminati spinning on the DMA-busy bit,
Omega Blast spinning forever on a VBlank-edge wait because that bit was wired to sprite-overflow
instead) — the corrected layout is credited to Nemesis's widely-cited reverse-engineered VDP
documentation. The V-counter's non-linear jump (0xEA → 0xE5 partway through VBlank, because 262
lines don't fit an 8-bit counter linearly) is real, well-documented hardware behavior and is
modeled exactly. The **H-counter has the same kind of jump**, and it's now modeled too: confirmed
against genesis-plus-gx's `cycle2hc32`/`cycle2hc40` tables (`core/hvc.h`), the visible HC byte range
skips a block of values partway through the line — H32 counts `0x00`-`0x93` then jumps to
`0xE9`-`0xFF`; H40 counts `0x00`-`0xB6` then jumps to `0xE4`-`0xFF`. `HorizontalCounter`
(`Vdp.HvCounter.cs`) reproduces that exact visible-value set and jump location, proportionally
spread across the scanline's dot index — hardware-accurate at the start/end of the line and the
jump's location, but **deliberately not a literal port of genesis-plus-gx's per-master-cycle
tables**: H40's real dot clock alternates between two different rates within a line (EDCLK), so the
true per-dot repeat pattern isn't uniform, and this emulator's coarser per-scanline cycle-budget
model doesn't have the master-clock-accurate timing needed to reproduce that exactly. If precise
dot-for-dot raster-timing tricks ever matter to a ROM you're testing, that non-uniformity is the
first place to look.

### 6.6 Rendering pipeline (`Vdp.Render.cs`)

`RenderScanline(scanline)`, per pixel, in priority order (highest to lowest): high-priority sprite,
high-priority plane A (nonzero color), high-priority plane B, low-priority sprite, low-priority
plane A, low-priority plane B, backdrop. Plane A's slot is replaced by the window plane wherever
the window is active for that row/column (checked via `IsWindowActiveVertically`/
`IsWindowActiveHorizontally`); plane B and sprites composite against the window exactly the same
way, using its own priority bit. Shadow/highlight "operator" sprites (palette line 3, color index
14 or 15) never render as visible colors themselves — they instead brighten or darken whatever
would otherwise be drawn underneath.

**Horizontal scroll addressing was the subject of a major, real-ROM-found bug** (the "Omega Blast
title-screen shear" investigation): the original formula packed per-row scroll-table entries 4
bytes apart; real hardware (confirmed against genesis-plus-gx's `hscroll_mask_table = {0x00, 0x07,
0xF8, 0xFF}`) packs them 32 bytes apart. A per-frame scroll update routine was writing to the
correct 32-byte-spaced offsets while the old read formula looked at entirely different addresses,
producing a visible shear where stale VRAM content leaked through. If you're debugging any kind of
horizontal-scroll visual artifact, re-read `ReadHScrollValue` first.

Vertical scroll has two modes: full-screen (`Vsram[plane]` directly) and per-2-column (`Vsram[(x>>4)*2 + plane]`
— one value per 16px group, 20 groups × 2 planes = exactly VSRAM's 40-word size).

### 6.7 Sprites (`Vdp.Sprites.cs`)

Sprites are a **linked list starting at sprite 0**, traversed via each entry's `Link` field — *not*
attribute-table order. Multi-cell sprites are laid out **column-major** in VRAM (all rows of
column 0, then column 1, ...), unlike the plane name tables, which are row-major; flip mirrors
across the sprite's *whole* width/height, not per-tile, so multi-cell flipped sprites still reorder
correctly.

**The masking quirk**: a sprite with X=0 (raw attribute-table X field, before the -128 offset) that
isn't the first sprite evaluated on a line causes the traversal to stop immediately — every sprite
after it in link order is simply not drawn on that scanline. This is real, documented Genesis VDP
behavior that some games rely on deliberately.

Overflow/collision: exceeding the per-line sprite count (20 H40 / 16 H32) or pixel-width budget
(320/256) sets the overflow status flag and stops evaluation; drawing over an already-occupied
pixel sets the collision flag (first-drawn sprite in link order wins and is never overwritten).

### 6.8 Window, interlace, shadow/highlight, Mode 4

- **Window** (`Vdp.Window.cs`): unscrolled — screen coordinates map directly onto its name table.
  The H32/H40 row-stride constant (32 cells / 64 cells) is confirmed against genesis-plus-gx's
  `vdp_ctrl.c` register-3 write handler and `vdp_render.c`'s row-address shift — real hardware
  pre-allocates a fixed 64-cell-wide window row in H40 even though only 40 columns are displayed.
- **Interlace** (`Vdp.Interlace.cs`): field-parity tracking plus IM2 tile addressing on planes,
  window, and sprites. Reworked from a naive "field picks tile A or tile B entirely"
  (`tileIndex*2+parity`) model to a paired-tile, sub-row-interleaved model derived by hand from
  genesis-plus-gx's `GET_LSB_TILE_IM2`/`GET_MSB_TILE_IM2` address arithmetic (`vdp_render.c:
  148-153`): the pattern name's top bit is dropped, the masked 10-bit index selects a *pair* of
  adjacent real VRAM tiles (2N top half / 2N+1 bottom half), and within whichever half a row
  falls in, each field samples every *other* physical row, offset by field parity. The old
  formula also had a genuine bug fixed by this rework — doubling the *full* 11-bit tile index
  produced an out-of-range address for any tile index ≥ 1024. Unlike every other area touched in
  this pass, **this one has no real ROM or reference output to verify it against** — it's
  documented in `Vdp.Interlace.cs` as a best-effort reconstruction from address arithmetic alone,
  not a confirmed fact, and remains the lowest-confidence corner of the mainstream rendering path
  for that reason (structurally closer to genesis-plus-gx now, but unverified in absolute terms).
  Sees essentially no real-world use regardless (Sonic 2's 2-player split screen is the most
  commonly cited example).
- **Shadow/highlight** (`Vdp.ShadowHighlight.cs`): confirmed against genesis-plus-gx's
  `palette_init()`/`make_lut_bgobj_ste()` — the real VDP DAC is a discrete 3-bit-per-channel model
  (shadow = raw component 0-7, normal = doubled 0-14, highlight = component+7, 7-14), which turns
  out to be numerically identical to this file's previous "halve/push-toward-white" approximation
  for every possible input. Also adds a previously-missing quirk: a sprite pixel using color index
  14 on palette lines 0-2 (not just line 3) draws its own color but is unconditionally immune to
  the default shadow rule. Color index 14 on palette line 3 specifically is deliberately left as
  the original "always highlight" simplification rather than changed — see the file's type-level
  remarks for why the reference source's own logic there is ambiguous without tracing an earlier
  compositing stage this investigation didn't cover.
- **Mode 4** (`Vdp.Mode4.cs`): SMS-compatibility mode, essentially unused by real Genesis software
  (exists for SMS-on-Genesis compatibility and VDP test suites). 4bpp *planar* tiles (not Mode 5's
  packed nibbles), a flat 64-entry sprite table with a sentinel terminator instead of a linked
  list, fixed 256×192 active area, and a documented quirk where the backdrop color comes from the
  *sprite* palette (line 1), not the background palette — this core structure is confirmed against
  genesis-plus-gx's `render_bg_m4`/`render_obj_m4`/`color_update_m4`, including confirming that a
  real SMS-chip-only tile-index-masking quirk is *correctly absent* here (it's gated off for any
  system above plain SMS in the reference source, which is the only configuration this emulator
  models). Two things that investigation surfaced but didn't resolve, flagged honestly in
  `Vdp.Mode4.cs`'s type-level comment rather than guessed at: a separate register-6-based sprite
  pattern-index mask that *does* apply on Genesis and isn't implemented here at all, and a
  possible mismatch in the background tile-index bit width / whether Mode 4 background tiles
  really support flipping the way this file assumes. Test coverage now includes both scroll-lock
  axes, large (8×16) sprites, sprite collision, scroll wraparound at the real boundary, tile flip,
  and both blanking paths — treat the two open structural questions above, not test coverage, as
  the first place to look if this file's confidence needs raising further.

### 6.9 Save state (`Vdp.SaveState.cs`)

Saves VRAM/CRAM/VSRAM/Registers, the port-protocol latch state, FIFO/DMA timing bookkeeping,
interlace field parity, and scanline/interrupt timing state. **Deliberately excludes `FrameBuffer`**
— it's pure rendered output, fully reproduced by the next `RenderScanline` call from state that
*is* saved.

---

## 7. Audio: YM2612 (FM) and PSG

`src/GenesisSharp.Core/Ym2612*.cs` and `Psg*.cs`. This is the subsystem with the deepest
reverse-engineering detail in the codebase — most of its register-level and synthesis-level
behavior has been explicitly cross-checked against genesis-plus-gx's `ym2612.c` (itself
Nuked-OPN2-derived) and community hardware documentation (smspower.org, Plutiedev), with several
concrete, cited corrections along the way. See §13 for the full citation list.

### 7.1 YM2612 register interface (`Ym2612.cs`)

Two independent 256-byte register banks (Part I = channels 1–3, Part II = channels 4–6), each with
its own address latch, mirroring the real two-port address/data-latch protocol:
```csharp
public void WriteAddressPart1(byte value);   // latches the address
public void WriteDataPart1(byte value);      // writes the latched register; a few addresses
                                              // (0x27 timer control, 0x28 key-on/off, 0x2A DAC
                                              // sample, 0x2B DAC enable) are special-cased
public void WriteAddressPart2(byte value);
public void WriteDataPart2(byte value);      // Part II has no special-cased addresses
public byte ReadStatus();                    // bit1/0 = Timer B/A overflow; busy bit hardwired 0
```
`Reset()` seeds both banks' pan registers (`0xB4`–`0xB6`) to `0xC0` (both L/R bits set) rather than
0 — a documented real power-on quirk (also replicated by BlastEm and Gens) that several real games,
Sonic 1's own "SEGA" DAC voice among them, rely on instead of setting pan explicitly. Without this,
Sonic 1's DAC sample would be silently computed but never audible on either channel.

### 7.2 FM synthesis engine (`Ym2612.Synthesis.cs`)

Each of the 6 channels' 4 operators has an `OperatorState`: `Phase` (0–1, whole cycles), an
`EnvelopePhase` (Attack/Decay1/Decay2/Release/Off), `AttenuationDb`, the last two raw sample outputs
(for self-feedback), and `LastSampleOutput` (for the algorithm's one-sample-delayed "MEM" path,
below).

**Envelope generator**: attenuation-in-dB tracking through Attack → Decay1 → Decay2 (sustain) →
Release → Off, using a smooth exponential rate-to-speed curve (an approximation, not the chip's
exact non-linear table) plus a real **key-scale-rate** term:
```csharp
ksr = keyCode >> (3 - rateScale);   // rateScale = the operator's RS field, always from its AR register
```
verified against genesis-plus-gx's `set_ar_ksr`/`eg_rate_select` indexing.

**Phase generator**: standard OPN F-Number formula, `frequencyHz = fnum * ClockHz / (144 * 2^(21 - block))`
— note the exponent is `21 - Block`; an earlier version briefly had `20 - Block` from unreliable
memory of the formula, making every note exactly one octave sharp. Detune is **key-code-shaped**,
not a flat cents offset: `DetuneShapeTable[dtField][keyCode]` reproduces genesis-plus-gx's `dt_tab`
per-key-code weighting, normalized against each row's own peak.

**Total Level**: 1.0 dB/step, not the commonly-repeated 0.75 dB figure (which belongs to a
different chip) — derived from genesis-plus-gx's fixed-point envelope math.

**The 8 FM algorithms** (`AlgorithmModulationSources`): each slot's modulation sources, and
critically, *which specific connections are one-sample-delayed* ("MEM" in real hardware — because
the chip processes operators in physical order S1, S3, S2, S4, not the "natural" S1–S4 order a
diagram suggests). This table — all 8 algorithms, including which of ALG3's two inputs into its
final slot is delayed and which isn't — is verified against genesis-plus-gx's
`setup_connection()`/`chan_calc()`. **If you ever touch this table, re-read the comment above it
first** — an earlier version had this as an unverified guess and even algorithm 0 was missing its
delay.

Modulation depth and feedback scale (`ModulationDepthScale = 2.0`, `FeedbackScale[fb] = 2^(fb-8)`)
are derived — not calibrated by ear — from **Nuked-OPN2**'s fixed-point conventions (13-bit signed
operator output, 20-bit phase accumulator). A previous version of this table was up to 4× too
strong at every feedback level and was implicated in exactly the kind of harsh, chaotic distortion
a feedback-heavy percussive sound effect would expose.

**DAC/PCM (channel 6)**: when the DAC is enabled, channel 6's normal FM output is replaced entirely
(not mixed) by the latched 8-bit unsigned PCM sample from register `0x2A`. Register `0x2A` is a
plain overwrite-on-write register on real hardware, not a FIFO — an earlier FIFO/queue design was
tried and reverted because it introduced audible discontinuities that don't exist on real hardware.

### 7.3 YM2612 timers (`Ym2612.Timers.cs`)

Timer A ticks once every **144** chip-clock cycles (verified against genesis-plus-gx's own
`eg_timer` comment, "chipclock/144/3" — an earlier, unverified `12.0` divisor made Timer A run 12×
too fast, which was enough to desync sound-driver-timed music). Timer B ticks 16× slower. Neither
timer is wired to a real interrupt — both are meant to be polled via `ReadStatus()`, which is how
real Genesis sound drivers actually use this chip.

### 7.4 PSG (`Psg.cs`, `Psg.Synthesis.cs`)

Write-only register-latch protocol matching the SN76489 family: bit 7 set = latch a register
(tone/noise select or channel volume), bit 7 clear = supply the high 6 bits of whichever tone
register was last latched. `Reset()` sets tone/noise to 0 but **volume to `0x0F`** (max
attenuation) — Sega's integrated PSG variant (as opposed to a generic discrete SN76489) is
documented to power on this way, and a live trace against real Sonic 1 confirmed frames 0–3 would
otherwise blare a near-max-volume burst on all four channels before the ROM's own boot code mutes
it at frame 4.

Attenuation is 2 dB/step (4-bit field, level 15 = exact silence). The noise channel's 16-bit LFSR
feeds back into **bit 15** specifically — confirmed against smspower.org's SN76489 reference for
the Sega-integrated variant; an earlier version fed back into bit 14 (one bit short of the register
width), degrading it to an effectively-15-bit LFSR with a shorter, differently-textured repeat
period than real hardware.

### 7.5 Save state

Both chips' `SaveState`/`LoadState` capture not just registers but continuously-advancing synthesis
state (phase, envelope position, the last two samples feeding delayed-modulation/feedback for
YM2612; tone phase and the LFSR register for PSG) — registers alone only tell you *targets*, not
where an oscillator or envelope currently is mid-flight, so a register-only save would produce an
audible glitch or desync on resume.

### 7.6 Mixing

`GenesisConsole.GenerateAudioSample()` sums PSG's mono output (duplicated to both channels) with
YM2612's native-stereo output, clipping to 16-bit signed range and counting clips via
`AudioClipCount`. Sample rate is 44,100 Hz.

---

## 8. Save states

`GenesisConsole.SaveState.cs` orchestrates every component's own `SaveState`/`LoadState` pair.

**File format**: a 4-byte magic (`"GSST"`), a version int (bumped whenever any component's layout
changes incompatibly), then a 32-byte SHA-256 fingerprint of the ROM the state was captured
against. (This is a pure identity check, not a security use of hashing — see the org security
policy note in §11 if you're wondering why that distinction matters here.) The fingerprint check
happens *before* any component state is touched, so a rejected load — wrong magic, wrong version,
or a ROM mismatch without `allowRomMismatch: true` — leaves the console completely untouched.

**Body**, in order: `Cpu`, `SoundCpu`, `Vdp`, `Ym2612`, `Psg`, both controller ports, `ExtPort`,
raw work RAM, raw Z80 RAM, the Z80 bank register and bus-arbitration flags, the TMSS register, and
finally the cycle/sample-debt accounting fields plus the audio diagnostic counters.

**Deliberately excluded**: the VDP's `FrameBuffer` (pure rendered output, regenerated by the next
scanline render) and the audio output queue (`_audioBuffer`, cleared on load rather than saved —
stale pre-load samples aren't meaningful game state).

**Threading contract**: safe to call between any two `RunFrame()` calls, never concurrently with
one in progress. The frontend enforces this by only ever performing save/load on the same
background thread that runs the emulation loop (§9) — see `MainForm.PerformPendingStateRequestOnEmulationThread`.

Verified via `GenesisConsoleSaveStateTests.cs`'s strongest test: run a real ROM 120 frames, save,
load into a **second, freshly-constructed** console for the same ROM, run both forward 60 more
frames, and assert the frame buffer and every dequeued audio sample are bit-identical. Any
unsaved or mis-restored field would eventually cause a divergence there.

---

## 9. The WinForms frontend

`src/GenesisSharp.Frontend/` — `MainForm.cs`, `DebugForm.cs`, `M68kDisassembler.cs`,
`Z80Disassembler.cs`, `DisassembledInstruction.cs`, `DisassemblyLabeler.cs`,
`GenesisAudioProvider.cs`, `Program.cs`.

### 9.1 Threading model

Emulation runs on a **dedicated background thread**, not the UI thread's timer. This was a
deliberate fix: an earlier version ran emulation from the UI `Timer`'s tick, and real-ROM testing
showed `AudioUnderrunCount` climbing whenever the UI thread's other duties (paint, message loop)
delayed a tick — audible as clicks/crackle. Now, `RunEmulationLoop()` runs on its own `Thread`,
paced by a `Stopwatch`-based fixed timestep (`1/60` second per frame, with a capped catch-up window
after a stall like a GC pause or machine sleep), and the UI thread's timer does *only* presentation
work: copying the frame buffer and refreshing the debug window.

**Every cross-thread interaction uses the same volatile-flag hand-off pattern**: the UI thread sets
a `volatile bool` request flag (plus, where needed, non-volatile payload fields written *before*
the flag and read only *after* it's observed true — a classic flag-as-memory-barrier idiom); the
emulation thread polls the flag once per loop iteration and performs the actual work on itself.
This pattern backs:
- **Pause / step-frame / step-instruction** (`_stepFrameRequested`/`_stepInstructionRequested`) —
  fire-and-forget, no completion signal needed.
- **Save/load state** (`_stateRequestPending`/`_stateRequestDone`/`_stateRequestError`) — the fuller
  version, since the UI thread needs to know when the operation finished and whether it failed. The
  UI thread busy-waits up to 5 seconds (`Thread.Sleep(2)` loop) rather than using
  `Application.DoEvents()`, which was explicitly considered and rejected.
- **Frame presentation** (`_frameSnapshot` + a lock, rather than a flag) — the emulation thread
  copies `Vdp.FrameBuffer` into a snapshot right after each frame; the UI timer reads the snapshot
  under the same lock, so it never observes a half-rendered frame.

If you add a new UI action that needs to touch console state, follow this same pattern — set a
flag, let the emulation thread do the actual work on itself, never call into `GenesisConsole` from
the UI thread directly.

**Falling behind real time** is handled in two places, because the two symptoms it produces are
unrelated to each other.

*Frame-skip*: the catch-up burst runs every frame the clock says is due but calls
`UpdateFrameSnapshot` only once, after the burst. Emulation is untouched — this drops *rendering*,
never emulated frames, so behavior is identical and only what reaches the screen changes.
Presenting is not free (a full 320×224 RGB24 copy), and during a burst every copy but the last was
being overwritten before the UI thread could read it, so the old code paid for invisible work
exactly when it had least headroom.

*Audio*: `GenesisAudioProvider` coasts from the last real sample with a short decay on underrun
rather than emitting silence. Cutting to zero puts a step discontinuity in the waveform — a click —
and a run of underruns becomes a click train, which is the "garbled" sound. Holding flat instead
gives a DC offset or a buzz at the refill rate. Decaying avoids both: a few missing samples are
smoothed over, a long gap fades to real silence. `AudioUnderrunCount` still counts every one, so
the condition stays diagnosable rather than smoothed out of existence.

Neither of these makes a slow build fast — see §9.8, which is the first thing to check. What they
do is make the failure *graceful and visible*: the window title shows `— behind Nf` while
emulation is actually behind, so a speed deficit reads as a speed deficit instead of presenting
identically to a broken sound chip.

### 9.2 Rendering

`CopyFrameBufferToBitmap()` swaps the VDP's RGB byte order into GDI+'s expected BGR order at
blit time, rather than changing the VDP's own `FrameBuffer` layout — because hundreds of existing
VDP tests assert against that layout byte-for-byte. `OnDisplayPaint` stretches the resulting bitmap
to the panel's current size with nearest-neighbor interpolation; window size only affects the
*initial* size, not runtime sharpness.

### 9.3 Input

Player 1 only: arrow keys → D-pad, Z/X/C → A/B/C, Enter → Start, applied directly to
`_console.ControllerPort1.Pad`.

### 9.4 Save-state UI

`&Save State...` (Ctrl+S) / `&Load State...` (Ctrl+L) menu items, filtered to `*.gss`, defaulting
to `<romname>.gss` next to the loaded ROM. On a ROM-fingerprint mismatch, prompts Yes/No before
retrying with `allowRomMismatch: true`. See §8 for the underlying format and §9.1 for the threading
mechanics.

### 9.5 DebugForm and the disassemblers

A non-modal debug window with a CPU tab (side-by-side 68000/Z80 disassembly + full register dump,
refreshed every UI tick), a 32X tab (both SH-2 cores' registers + disassembly, plus nRES/ADEN
status — see §4a.4), and a VRAM tab (live tile viewer against any of the 4 CRAM palette lines).
`M68kDisassembler`/`Z80Disassembler`/`Sh2Disassembler` are **static, display-only** classes
deliberately kept separate from the real CPUs' decode/execute logic — a bug in a disassembler can't
affect emulation correctness, and any opcode they don't recognize falls back to a raw hex dump
instead of a guessed mnemonic. All three annotate known memory-mapped I/O addresses (VDP ports,
PSG, controller ports, the Z80 bank register, 32X registers, etc.) as trailing comments, sourced
from `GenesisConsole`'s/`Sega32X`'s own bus implementation rather than general hardware lore — so
if you add a new memory-mapped register, consider adding it to the relevant disassembler's
annotation table too.

The CPU tab also has a **68000 address-peek** field (Phase 6, added during real-ROM debugging —
see §4a.5): typing an address and clicking "Go" shows a fixed disassembly range there instead of
following the live PC, and the register box includes a raw stack-top peek (`Stack@A7: +00=... +04=...`).
Neither reads anything the emulator doesn't already expose publicly — they exist purely to let a
human find a caller's address (e.g. from a stack-peeked return address) without needing a real
breakpoint/step-until feature, which this debug window doesn't have. "Follow PC" clears the
override back to normal live-following behavior.

### 9.6 Audio playback

`GenesisAudioProvider` implements NAudio's `IWaveProvider`, draining
`TryDequeueBufferedAudioSample` into 16-bit stereo PCM and emitting silence (not a stale sample) on
underrun. Played via `WasapiOut` at 20ms latency — WASAPI shared mode's practical floor is
around 10ms; going lower trades underrun risk for less lag.

### 9.7 Command-line debug tools (`tools/GenesisSharp.DebugTools`)

Two companions to the debug window, for the questions it answers badly. Both were built while
root-causing real bugs in a commercial 32X title and kept because the next such investigation will
want them again. Neither is referenced by the emulator or the frontend — they're consumers, so
nothing here can affect emulation correctness.

**`disasm <rom> <sh2-address-hex> [count]`** disassembles SH-2 code straight out of a cartridge
image with no emulator running. A 32X header's Initial Data Load descriptor (ROM `$3D4`: source,
destination, length) tells the boot code which ROM range to copy into SDRAM, so a game's
SDRAM-resident code is a straight copy of a known ROM range — `IdlRomBus` resolves `$06xxxxxx`
through that descriptor, and `$02xxxxxx` straight through to the cartridge. It link-compiles the
frontend's own `Sh2Disassembler` (the frontend targets `net9.0-windows`, so it can't be
project-referenced from a console tool) so the output is byte-identical to the debug window's.
Limitation worth knowing: this is the ROM's *initial* image, so anything the game decompresses or
overwrites at runtime needs the live peek instead.

**`probe <rom> [frames]`** runs a ROM headlessly and reports 32X display state once per frame,
printing only when it changes. The load-bearing number is how many of the 224 scanlines each
frame-buffer bank's line table can actually *resolve* — because the 32X frame buffer isn't a plain
bitmap (see §4a.1), a bank full of pixel data but with an empty line table renders as nothing, and
looks identical to an empty bank from a screenshot. `FS` is deliberately excluded from the
change-detection key and reported as a flip count instead: a page-flipping title toggles it every
frame, which would make every frame a "change" and bury the signal.

That distinction is the whole point. The FM-ownership bug above (§4a) reduced to four lines of
`probe` output — one bank stuck at `lines=0` forever while both received pixel data, with `FS`
flipping every frame — after the same question had taken a long sequence of single-address peeks
through the UI to ask badly. When 32X output looks wrong, run `probe` before stepping frames by
hand.

### 9.8 Build configuration and real-time pacing

**Run a Release build.** This is not the usual "Release is a bit quicker" advice — for 32X titles
it is the difference between working and not. Measured headlessly, 600 frames = 10.0s of emulated
time:

| Title | Debug | Release |
| --- | --- | --- |
| Sonic (no 32X) | 10.8s — **0.92×** real time | 2.1s — 4.8× |
| Pitfall (32X) | 32.2s — **0.31×** real time | 6.2s — 1.6× |

Emulation is deterministic, so a slow build never changes what is *emulated* — but audio is
generated on the emulation clock and drained by WASAPI in real time, and there is no frame-skip or
audio catch-up path. At 0.31× roughly 69% of the samples the audio device asks for were never
produced, and `GenesisAudioProvider` emits silence on underrun, so playback becomes silence
interleaved with fragments at a very high rate. That is audibly indistinguishable from a broken
sound chip, and was in fact misdiagnosed as one — the 32X PWM chip was investigated at length
before a measurement showed the game never programs PWM at all during the affected scenes, and
that the emulator's own sample output was in the same amplitude range as titles that sounded fine.

Debug's ~5× penalty barely matters for a plain Genesis title (0.92× reads as full speed), which is
what made this look 32X-specific: adding two SH-2 cores at 23MHz is roughly a 20-40× increase in
interpreter work, so 32X is where the margin runs out first. Before attributing any speed or audio
problem to emulation accuracy, check which configuration is running.

Release headroom for 32X (1.6×) is still much thinner than for plain Genesis (4.8×), so a heavy
32X scene can dip below real time even there — see the SH-2 interpreter throughput notes in §4a.

---

## 10. Testing strategy

`tests/GenesisSharp.Tests/` — 39 files, ~381 `[Fact]`/`[Theory]` tests, referencing the CPU
projects and `Core` directly (not `Frontend` — `MainForm`/`DebugForm` have no automated coverage;
UI changes are verified by build + manual smoke-testing).

Grouped by subsystem: 68000 CPU (10 files), Z80 CPU (4 files), VDP (11 files), audio (4 files),
console/system integration (6 files, including `GenesisConsoleSaveStateTests.cs`), I/O peripherals
(3 files). Four helper files (`FlatMemoryBus.cs`, `FlatZ80Bus.cs`, `Asm.cs`, `Z80Asm.cs`) provide a
minimal `IBus` stub and tiny in-test assemblers so CPU-level tests can write real machine code
inline rather than hand-encoding opcodes.

**Real-ROM integration testing**: `SagaRoms/` at the repo root holds real commercial/homebrew ROMs
(`sonic.bin`, `omega_blast.bin`, Shadow of the Beast, Sampras Tennis 96, PRINGLES, and others) used
both for automated save-state round-trip tests and as the standard set of "does this still work"
smoke-test targets whenever you change VDP/CPU/audio behavior. `tests/GenesisSharp.Tests/TestRoms/`
is scaffolding for future ROM-based fixtures (currently just a `.gitkeep`), wired into the `.csproj`
via a `CopyToOutputDirectory` item but otherwise unused.

**Convention for this project** (see also §11): when you fix a hardware-behavior bug, prefer adding
both a targeted unit test *and* running the relevant real ROM(s) for several hundred frames as a
smoke test — several of the most important fixes in this codebase (H-scroll addressing, VDP status
bits, YM2612 timers/algorithms) were only caught this way, not by a unit test alone.

---

## 11. How to extend the emulator

A few concrete, common extension scenarios and where to start:

**Add a new memory-mapped device or register.**
Add the address-range check and read/write handler to `GenesisConsole`'s `IBus` implementation
(§5.2/§5.3) — never to either CPU core. If it's Z80-visible too, add it to the Z80-side `IBus`
methods as well. Give it a `SaveState`/`LoadState` pair if it has any state beyond what's already
captured, and add it to `GenesisConsole.SaveState.cs`'s ordered write/read list (bump
`SaveStateVersion` if you do — old save files with the previous layout will then be cleanly
rejected instead of read as garbage). Consider adding an annotation entry to the frontend
disassemblers (§9.5) too.

**Add cartridge header parsing / bank-switching mappers.**
`Cartridge.cs` is intentionally minimal scaffolding today (§5.7) — this is the most clearly-flagged
"not yet done" area of the whole codebase. A mapper would likely need: header detection in
`Cartridge.LoadFromBin`, a mapper abstraction analogous to how `NesSharp.Core`'s `IMapper` works
(a similar problem, different console — worth a look for design inspiration even though the code
itself isn't shared), and `GenesisConsole`'s ROM-space read/write handlers routed through it
instead of the current flat `% Rom.Length` mirroring.

**Fix or improve a VDP timing/rendering detail.**
Start in whichever `Vdp.*.cs` file owns it (§6 has a per-file breakdown), and check whether
genesis-plus-gx or BlastEm's source (both cloned under `reference/`) already documents the correct
behavior before guessing — this codebase's most valuable fixes have all come from exactly that kind
of comparison, not from re-deriving hardware behavior from first principles. If you find and fix a
similar bug, follow the existing convention: name the real ROM that exposed it, cite the exact
reference-source detail that confirmed the fix, and add a regression test.

**Add or fix FM/PSG synthesis behavior.**
`Ym2612.Synthesis.cs`'s type-level doc comment and the citations throughout it (§7, §13) are the
map of what's been verified vs. approximated — LFO, SSG-EG, and channel-3 "special mode" are the
most clearly still-unmodeled features if you're looking for a next FM improvement. `AlgorithmModulationSources`
and the KSR/detune tables are the highest-value, best-verified reference material to build from.

**Add a UI feature to the frontend.**
Follow the volatile-flag hand-off pattern (§9.1) for anything that touches `GenesisConsole` state
from a menu item or dialog — do not call into the console directly from a UI event handler.

**A quick orientation checklist for a first change:**
1. Read this document's section for the subsystem you're touching.
2. Skim the relevant source file's type-level doc comment — most files here have one, and it
   usually states confidence level and known gaps up front.
3. Check `reference/genesis-plus-gx` (and `reference/SGDK` for real-world consumer behavior, e.g.
   what status bits SGDK's own runtime polls) before guessing at hardware behavior.
4. Add or update a test in `tests/GenesisSharp.Tests`, and run the relevant `SagaRoms/*.bin` ROM
   for a few hundred frames as a smoke test if the change is behavior-affecting.
5. `dotnet build GenesisSharp.sln` then `dotnet test GenesisSharp.sln` before considering the
   change done.

---

## 12. Known limitations and low-confidence areas

A consolidated list, pulled from §3–§9, of what to check first if a game misbehaves:

- **Cartridge**: no header parsing, no bank-switching mappers, no interleaved `.smd` support.
- **68000**: CMPM misdecodes as EOR; NEGX unimplemented; RTE not privilege-checked; DIVU/DIVS
  timing is an average-case approximation, not the real data-dependent formula.
- **Z80**: no undocumented IXH/IXL/IYH/IYL; X/Y flag bits are an approximation; DD/FD timing
  constants aren't instruction-by-instruction verified; block I/O flags are simplified.
- **VDP**: the H-counter's visible-value range and jump location are confirmed against
  genesis-plus-gx, but its exact dot-for-dot repeat pattern within a line — especially H40's
  non-uniform EDCLK-driven pixel clock — is not independently verified (see §6.5). Interlace
  (IM2)'s addressing has been reworked to structurally match genesis-plus-gx's real formula (see
  §6.8), fixing a genuine out-of-range-tile-index bug along the way, but — uniquely among
  everything touched in this pass — it has no real ROM or reference output to check the result
  against, so it remains the lowest-confidence corner of the whole VDP despite being closer to
  correct than before. Mode 4's core structure is now confirmed against genesis-plus-gx and has
  substantially better test coverage, but two specific structural questions (a Genesis-applicable
  sprite pattern-index mask this file doesn't implement, and a possible background tile-index
  bit-width/flip mismatch) remain open — see `Vdp.Mode4.cs`'s type-level comment and §6.8.
  (Window row-stride and shadow/highlight brightness math were previously listed here too but are
  now confirmed against genesis-plus-gx — see §6.8. Shadow/highlight's one remaining open question,
  the exact behavior of color index 14 on palette line 3, is called out specifically in
  `Vdp.ShadowHighlight.cs`.)
- **YM2612**: LFO, SSG-EG, and channel-3 "special mode" are entirely unmodeled. The rate-to-dB
  envelope curve is a smooth exponential approximation, not the chip's exact non-linear table.
- **SH-2 / 32X**: the SH-2 CPU core (§4a), its bus integration (§4a.1), its frame-buffer graphics
  (§4a.2), its PWM audio (§4a.3), its frontend/save-state integration (§4a.4), and a synthesized
  boot-stub fallback for real ROMs (§4a.5) are Phases 1-6 of an in-progress 32X extension — both
  SH-2s can be released from reset and exchange data with the 68000 via COMM registers, a 32X
  title's own Packed Pixel/Direct Color frame-buffer output composites correctly with the Genesis
  VDP's, its PWM output is audibly mixed in, the whole subsystem round-trips through save states,
  the debug window has a live SH-2 register/disassembly tab, and — as of §4a.5 — a real,
  unmodified 32X ROM's boot sequence (the "M_OK"/"S_OK" COMM-register handshake, jumping to each
  core's own ROM-header-named entry point) runs without needing a real Sega BIOS this project
  can't ship. All three display modes, all five 32X interrupt sources (§4a.6), the on-chip DMAC
  including PWM's own RTP-driven auto-feed, and real-ROM-verified frame-buffer rendering have since
  landed too. Within the CPU core itself: no cache/timer/
  serial emulation; NMI's vector number and interrupt-entry cost are unverified (PicoDrive doesn't
  model SH-2 NMI); no hardware test-vector suite is known to exist for SH-2, unlike the 68000 core.
  Phase 6's boot-stub mechanism has synthetic test coverage (§4a.5) but has not yet been confirmed
  against an actual commercial 32X ROM dump.
- **General**: no true whole-system single-instruction step (the frontend's "step instruction" is
  68000-only, by design — a real cross-chip single step would need sub-scanline stepping support
  that doesn't exist yet); PAL timing/60Hz-vs-50Hz is entirely unmodeled (NTSC-only).

---

## 13. References and credits

GenesisSharp's hardware behavior was built primarily from general knowledge of the Genesis/Mega
Drive platform, then **corrected and verified** against the following external sources wherever a
specific claim needed checking or a bug needed root-causing. Every entry below is cited from an
actual in-source comment (file:line references point at the specific claim, not just "somewhere in
this file") — search the codebase for the project name if you want the full surrounding context.

### Reference implementations vendored under `reference/` (source comparison only — never built or linked)

**Genesis Plus GX** — `reference/genesis-plus-gx/`
The single most-cited source in this codebase. A mature, actively-maintained open-source
Genesis/Mega Drive/Sega CD/Master System/Game Gear/SG-1000 emulator.
- © 1998–2003 Charles MacDonald (original "Genesis Plus"), continued 2007–2026 by Eke-Eke; some
  portions © Nicola Salmoria and the MAME team.
- License: a custom source-available redistribution license — **non-commercial use only**,
  modified redistributions must include complete source. See `reference/genesis-plus-gx/LICENSE.txt`
  for the full text. (That file also documents the licenses of several libraries Genesis Plus GX
  itself bundles for its various platform ports — Nuked-OPN2, Tremor, minimp3, libCHDR, zlib, zstd,
  and others — none of which GenesisSharp uses directly; only the ones separately cited below were
  consulted.)
- Used to verify/root-cause: the VDP H-scroll table addressing formula; VDP DMA access-slot timing
  (including the copy-vs-fill 2× throughput difference); the VDP status register's DMA-busy bit
  behavior; the VDP window plane's H32/H40 name-table row stride; the VDP's discrete 3-bit-per-
  channel shadow/highlight DAC model and its sprite color-index-14 operator quirks; the H-counter's
  visible-value range and jump location for both H32 and H40; Mode 4's core rendering structure
  (and, separately, two specific Mode 4 details this investigation flagged as still open rather
  than resolved — see `Vdp.Mode4.cs`); the general shape of IM2 interlace's paired-tile addressing
  (root-caused a genuine tile-index-range bug along the way, though the resulting scheme is a
  hand-derived reconstruction, not independently confirmed — see `Vdp.Interlace.cs`); the
  68000-side Z80 bus-request register's "prefetch noise on
  unused bits" quirk; the YM2612's Timer A tick rate, Total Level dB step size, key-code fraction
  table, key-scale-rate formula, detune table shape, and — most extensively — the exact
  operator-connection graph (and one-sample-delay behavior) for all 8 FM algorithms.

**Nuked-OPN2** (`ym3438.c`) — vendored inside genesis-plus-gx, not separately cloned
A cycle-accurate reverse-engineered YM2612 (OPN2) core; genesis-plus-gx's own FM engine is itself
derived from it.
- © 2017–2022 Alexey Khokholov ("Nuke.YKT"). License: LGPL v2.1 (see the excerpt in
  `reference/genesis-plus-gx/LICENSE.txt`).
- Used to derive (not just approximate) the YM2612's modulation-depth and feedback-depth scaling
  constants, from its documented 13-bit signed operator output and 20-bit phase accumulator
  conventions.

**SGDK** (Sega Genesis Development Kit) — `reference/SGDK/`
A free, actively-used C SDK for Genesis/Mega Drive homebrew development, by Stephane Dallongeville.
- License: MIT (the SDK library itself; see `reference/SGDK/license.txt`). Bundled GCC/libgcc
  components are separately under GPLv3 with the GCC runtime exception
  (`reference/SGDK/COPYING.RUNTIME`) — not consulted by this project.
- Used as a real-world *consumer* of VDP hardware timing: SGDK's own `VDP_waitDMACompletion`
  function polls the VDP status register's DMA-busy bit, which is part of why that bit's exact
  timing behavior matters for homebrew compatibility, not just for specific commercial ROMs.

**PicoDrive** — `reference/PicoDrive/picodrive/`
An open-source Genesis/Mega Drive/32X/Sega CD/SG-1000/Master System/Pico emulator, originally built
for ARM-based handhelds, by Gražvydas "notaz" Ignotas. The only vendored reference with real 32X
and SH-2 support — genesis-plus-gx has never shipped either — so it is the sole ground truth for
§4a's SH-2 CPU core. Its SH-2 interpreter (`cpu/sh2/mame/sh2.c`) is itself MAME's portable SH-2
core, © Juergen Buchmueller, freeware for non-commercial use.
- License: the same non-commercial, source-available shape as genesis-plus-gx's — redistributions
  may not be sold or used commercially, and modified redistributions must include complete source.
  See `reference/PicoDrive/picodrive/COPYING` for the full text.
- Used to confirm/derive: every SH-2 opcode's bit pattern and dispatch grouping; delayed-branch and
  illegal-slot-instruction semantics; DIV0S/DIV0U/DIV1's Q/M/T bit manipulation; MAC.L/MAC.W's
  saturation logic; ADDC/SUBC/ADDV/SUBV/NEGC's carry/overflow bookkeeping; SR's flag-bit layout and
  the FLAGS mask used by LDC/RTE; and exact per-instruction cycle costs (including PicoDrive's own
  "timing is a guess" caveats on TRAPA/ILLEGAL, reproduced here rather than silently upgraded to
  false confidence).

### Community hardware documentation and other emulators (cited by name, not vendored)

- **BlastEm** (`vdp.c`) — a cycle-accurate open-source Genesis/Mega Drive emulator by Michael
  Pavone, described in this codebase's comments as "one of the few emulators accurate enough to
  pass Nemesis's VDP FIFO test ROM." Used to confirm the VDP's 4-entry command FIFO depth and its
  per-dot access-slot model, and (together with Gens) the YM2612's pan-register power-on default.
- **Nemesis's VDP FIFO test ROM** — a community-authored Genesis hardware test ROM; referenced via
  BlastEm's own documented success against it, as indirect validation of this emulator's FIFO
  model. Also the source credited (via BlastEm's own citation) for the VDP status-register bit
  layout used after two real-ROM regressions exposed the previous, incorrect layout.
- **clown68000** — an open-source, cycle-accurate 68000 core by "clownacy," itself validated against
  the **SingleStepTests/m68000** hardware-captured test-vector suite
  (https://github.com/SingleStepTests/m68000). Used to cross-check DIVU/DIVS divide-by-zero and
  overflow flag behavior.
- **smspower.org** SN76489/PSG hardware reference — community wiki documentation. Used to confirm
  the Sega-integrated PSG's power-on register defaults (volume registers at max attenuation, not
  zero) and the noise channel's 16-bit LFSR feedback-tap position (bit 15).
- **smspower.org / Plutiedev** YM2612 register references (Plutiedev is Sik's technical blog on
  Mega Drive hardware) — used to confirm the F-Number-to-frequency formula's exponent and the
  YM2612's real physical per-operator register byte ordering (S1, S3, S2, S4 — not the "natural"
  S1–S4 order the algorithm diagrams suggest).
- **Charles MacDonald's `genvdp.txt`** — a classic, widely-circulated community VDP hardware
  documentation text file (by the same author who originally wrote Genesis Plus, later developed
  into genesis-plus-gx). The general register and command-word bit-layout starting point for this
  emulator's VDP, explicitly flagged in-source as "a reasonable first draft to verify, not a settled
  fact" rather than independently re-confirmed line-by-line.
- **Nemesis's reverse-engineered VDP status-register documentation** — cited by name as the source
  used to rebuild the status register's bit layout after real-ROM regressions exposed the original
  guess as badly scrambled.

### Empirical / real-hardware validation

Several fixes in this codebase were found or confirmed not from a document but from **running real
ROMs** and observing the mismatch directly — these are named in the source comments as the
motivating test case, not as a citable external "source," but are worth listing since they're how
several of the bugs above were actually caught: **Omega Blast** (H-scroll shear, VDP command-word
bit fix, DMA-busy timing, VBlank status bit), **Shadow of the Beast** (Z80 bus-request prefetch
quirk), **Scorpion Illuminati** (VDP DMA-busy status bit, open-bus fallback behavior), **Sonic The
Hedgehog** (work-RAM mirroring range, YM2612 pan-register power-on default, PSG volume power-on
default), **Sampras Tennis 96** and **Crazy Driver** (68000 vs. Z80 vblank-interrupt level
distinction).

### What is *not* independently verified

For completeness, and so future contributors know where to focus verification effort: the Z80
core's opcode timing/flag tables are built from general platform knowledge without a specific
cited external source confirming them (see §12). If you find an authoritative source for this,
updating the relevant file's doc comment — and this document — with the citation is exactly the
kind of contribution this codebase's existing comments model.

Mode 4 rendering was in this list too until its core structure was traced to genesis-plus-gx's
`render_bg_m4`/`render_obj_m4`/`color_update_m4` — see §6.8 and `Vdp.Mode4.cs`. That same
investigation surfaced two specific pieces it couldn't resolve rather than papering over: a
register-6-based sprite pattern-index mask genesis-plus-gx applies on Genesis-class hardware that
this file doesn't implement at all, and a possible mismatch between this file's 9-bit background
tile index (plus separate flip bits) and genesis-plus-gx's 11-bit mask for the same field. Both are
called out precisely, with line citations, in `Vdp.Mode4.cs`'s type-level comment.

The H-counter's visible-value range and jump location were in this list too until they were traced
to genesis-plus-gx's `cycle2hc32`/`cycle2hc40` tables (`core/hvc.h`) — see §6.5. What's still
unverified there is the exact dot-for-dot *repeat pattern* within a line: genesis-plus-gx's own
tables show H40's real pixel clock alternating between two different rates (EDCLK), which this
emulator's coarser per-scanline cycle-budget model doesn't reproduce — the fix ported the correct
value *set* and jump boundary, proportionally spread across the line, rather than a literal
per-master-cycle table.

The shadow/highlight brightness formula was in this list too until it was traced to
genesis-plus-gx's `palette_init()`/`make_lut_bgobj_ste()` (`vdp_render.c:797-921,973-1149`) — see
§6.8. One piece of that investigation stayed unresolved on purpose: `make_lut_bgobj_ste`'s handling
of sprite color index 14 on palette line 3 branches on an *incoming* background intensity state
(`bx & 0x80`) produced by an earlier stage (`make_lut_bg`) that wasn't traced. Both literal readings
of that branch suggest the common case actually resolves to normal brightness, not highlight, which
would contradict long-cited community documentation (e.g. Charles MacDonald's genvdp.txt) — since
that's a real conflict rather than a gap, this codebase deliberately kept the original "always
highlight" behavior rather than flip it on an unverified re-derivation. Tracing `make_lut_bg` to
resolve that conflict is a good next step for whoever picks this up.

IM2 interlace tile addressing is a different case from the three above: it *was* traced to
genesis-plus-gx's `GET_LSB_TILE_IM2`/`GET_MSB_TILE_IM2` (`vdp_render.c:148-153`), and that tracing
did fix a genuine bug (the old code doubled the full 11-bit tile index instead of masking to 10
bits first, corrupting addressing for any tile index ≥ 1024) — but the resulting paired-tile,
sub-row-interleaved addressing scheme in `Vdp.Interlace.cs` was hand-derived from address
arithmetic, not read off an explanatory comment the way the other three were, and there is no real
ROM or independently-known-correct reference output anywhere to check it against. It's the one
piece of this remediation pass that stays explicitly labeled "best-effort reconstruction" rather
than "confirmed," and is flagged as such in `Vdp.Interlace.cs`'s own type-level comment.
