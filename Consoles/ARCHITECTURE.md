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
  GenesisSharp.Core/       — net9.0, depends on Cpu68000 + CpuZ80
  GenesisSharp.Frontend/   — net9.0-windows, WinForms, depends on Core, NAudio 2.3.0
tests/
  GenesisSharp.Tests/      — net9.0, xUnit, depends on Cpu68000 + CpuZ80 + Core (not Frontend)
SagaRoms/                  — real commercial/homebrew ROMs used by integration tests & manual testing
reference/
  genesis-plus-gx/         — cloned reference emulator, used only for source comparison (never built/linked)
  SGDK/                    — cloned homebrew SDK, used only for source comparison
```

Dependency direction is strictly one-way: `Cpu68000`/`CpuZ80` → `Core` → `Frontend`. `Frontend`
never references either CPU project directly — it only ever sees `Cpu68000.IBus`/`CpuZ80.IBus`
(aliased in `DebugForm.cs` as `Cpu68000Bus`/`Z80Bus`) through `Core`. `Tests` references the CPU
projects and `Core` directly (for low-level per-instruction unit tests) but not `Frontend`, since
`MainForm`/`DebugForm` have no automated coverage — see §10.

`reference/genesis-plus-gx` and `reference/SGDK` are **not build dependencies of anything** — they
are git clones kept around purely so that comments in this codebase can say "confirmed against
genesis-plus-gx's `vdp_ctrl.c`" and you can go read that file yourself. See §13 for their licenses.

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
modeled exactly; the **H-counter's equivalent jump quirk is explicitly not modeled with the same
confidence** — it's a linear approximation across the scanline's dot count, monotonic but not
verified against real breakpoints for H32 vs H40. If precise raster-timing tricks ever matter to a
ROM you're testing, this is the first place to look.

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
- **Interlace** (`Vdp.Interlace.cs`): field-parity tracking plus IM2 tile-index doubling on planes,
  window, and sprites. Sees essentially no real-world use (Sonic 2's 2-player split screen is the
  most commonly cited example) and is explicitly the lowest-confidence corner of the mainstream
  rendering path.
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
  *sprite* palette (line 1), not the background palette. Treat this file as the least-tested part
  of the whole VDP.

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
refreshed every UI tick) and a VRAM tab (live tile viewer against any of the 4 CRAM palette lines).
`M68kDisassembler`/`Z80Disassembler` are **static, display-only** classes deliberately kept separate
from the real CPUs' decode/execute logic — a bug in a disassembler can't affect emulation
correctness, and any opcode they don't recognize falls back to a raw hex dump instead of a guessed
mnemonic. Both annotate known memory-mapped I/O addresses (VDP ports, PSG, controller ports, the
Z80 bank register, etc.) as trailing comments, sourced from `GenesisConsole`'s own bus
implementation rather than general hardware lore — so if you add a new memory-mapped register to
`GenesisConsole`, consider adding it to the disassembler's annotation table too.

### 9.6 Audio playback

`GenesisAudioProvider` implements NAudio's `IWaveProvider`, draining
`TryDequeueBufferedAudioSample` into 16-bit stereo PCM and emitting silence (not a stale sample) on
underrun. Played via `WasapiOut` at 20ms latency — WASAPI shared mode's practical floor is
around 10ms; going lower trades underrun risk for less lag.

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
- **VDP**: H-counter dot-exact timing is a linear approximation, not verified hardware breakpoints;
  Mode 4 and interlace (IM2) are the least-real-world-tested rendering paths in the whole VDP.
  (Window row-stride and shadow/highlight brightness math were previously listed here too but are
  now confirmed against genesis-plus-gx — see §6.8. Shadow/highlight's one remaining open question,
  the exact behavior of color index 14 on palette line 3, is called out specifically in
  `Vdp.ShadowHighlight.cs`.)
- **YM2612**: LFO, SSG-EG, and channel-3 "special mode" are entirely unmodeled. The rate-to-dB
  envelope curve is a smooth exponential approximation, not the chip's exact non-linear table.
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
  channel shadow/highlight DAC model and its sprite color-index-14 operator quirks; the 68000-side
  Z80 bus-request register's "prefetch noise on unused bits" quirk; the YM2612's Timer A tick rate,
  Total Level dB step size, key-code fraction table, key-scale-rate formula, detune table shape, and
  — most extensively — the exact operator-connection graph (and one-sample-delay behavior) for all
  8 FM algorithms.

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
core's opcode timing/flag tables, the VDP's H-counter dot-exact breakpoints, Mode 4 rendering, and
IM2 interlace tile-doubling are all built from general platform knowledge without a specific cited
external source confirming them (see §12). If you find an authoritative source for any of these,
updating the relevant file's doc comment — and this document — with the citation is exactly the
kind of contribution this codebase's existing comments model.

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
