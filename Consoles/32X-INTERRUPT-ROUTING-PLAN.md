# 32X interrupt routing (VRES/VINT/HINT/CMD/PWM) — phased implementation plan

## Context

Phase 2 of the original 32X plan explicitly deferred all 32X-side interrupt routing wholesale
("not required for the stated verification test... left for whenever real ROMs or a later
sub-step need them"). That moment has arrived: live-verified tracing against a real commercial
32X title (Pitfall: The Mayan Adventure), after fixing three real bugs found the same way
(COMM4 checksum seed, VERSION register/`nPAL` region-detection polarity, `nPAL`'s write
protection), gets the game substantially further into its own boot sequence than ever before —
past the region checks, past the checksum handshake, into normal 68000/SH-2 operation with
interrupts genuinely unmasked (`SR` IPL dropped from 7 to 1-2 on all three cores, a first in this
whole investigation). It then deadlocks: the 68000 spins on `TST.W 32(A5)` (COMM0, `$A15120`)
waiting for it to become zero, while it currently holds `0x0002` — a value that looks exactly
like a command code one side wrote for the other to notice and act on. Both SH-2 cores are still
actively running (not stuck in an obvious trap) but never reach whatever code would clear that
flag. This is the shape of a genuine missing-interrupt gap, not a bug in already-implemented
logic — GenesisSharp currently has no delivery mechanism at all for any of the five 32X
interrupt sources.

This is real new-feature scope, comparable to a phase of the original 32X plan, not a quick
patch — hence broken into phases the same way, each independently verifiable and each landing
real, working behavior rather than one large all-or-nothing change.

## Ground truth (confirmed against `reference/PicoDrive/picodrive/pico/32x/` — all five Phase 0
questions closed, see Phase 0 below for how each was resolved)

- **All five interrupt sources are SH-2-side only.** Per `pico_int.h:624-628`:
  `P32XI_VRES`/`P32XI_VINT`/`P32XI_HINT`/`P32XI_CMD`/`P32XI_PWM`. Searched exhaustively: no
  `SekInterrupt`/similar call site anywhere in `32x.c`/`memory.c`/`pwm.c` raises an interrupt on
  the 68000 itself from a 32X event. The 68000 is expected to be notified of SH-2 activity by
  **polling** COMM registers, not by receiving its own 32X-sourced interrupt — matching exactly
  what the live trace shows (it's the 68000 stuck polling, the SH-2s that need waking).
- **The full CMD protocol, confirmed end-to-end** (this is what the live stall is actually
  waiting on): the 68000 writes a command value into a COMM register (COMM0 in the live trace),
  then writes to adapter-block offset 3 (`$A15103`, "irq ctl") to request a CMD interrupt on one
  or both SH-2 cores (`memory.c:429-436`, bits 0/1 = request-to-master/request-to-slave, only
  takes effect if those bits actually change). Each core only actually receives the pending
  interrupt if its own mask bit allows it — live AND, recomputed on every relevant write, not a
  one-shot latch (`p32x_update_cmd_irq`, `32x.c:89-102`). The SH-2, once it services the
  interrupt, clears its own request bit back (`memory.c:946`) and (per the game's own protocol,
  not a hardware requirement) presumably also clears the COMM register it read the command from.
  The 68000's readback of the same offset (`p32x_reg_read16`, `memory.c:346-378`, the `a==2`
  case, commented "INTM, INTS") is a plain read of those same bits — this is what the polling
  loop actually observes clearing.
- **Priority/vector table, fully confirmed** by hand-tracing `p32x_update_irls`'s binary-search
  priority encoder (`32x.c:34-74`) against the bit positions in `pico_int.h:624-628`, and cross-
  checked against `sh2_irq_cb`'s auto-vector formula (`32x.c:18-31`, `return 64 +
  pending_irl/2` — the real SH-2 hardware convention for auto-vectored external interrupts with
  no explicit vector asserted):

  | Source | Pending bit | SH-2 IRL level | Auto-vector number |
  |---|---|---|---|
  | VRES | `0x80` | 14 (highest) | 71 |
  | VINT | `0x40` | 12 | 70 |
  | HINT | `0x20` | 10 | 69 |
  | CMD  | `0x10` | 8  | 68 |
  | PWM  | `0x08` | 6 (lowest) | 67 |

  This maps directly onto GenesisSharp's existing `Sh2.RaiseInterrupt(level, vectorNumber)` (built
  in the original SH-2 core plan, unused for 32X until now) with **zero changes needed to the
  `Sh2` core itself** — that method's own doc comment already anticipated exactly this: "external
  interrupt vector numbers are configurable by whatever's wiring the interrupt controller, a
  32X-specific concern out of scope for this core."
- **SH-2 IRQ mask register, fully confirmed, including a real asymmetry GenesisSharp doesn't
  model at all yet**: `memory.c:824-839` (`p32x_sh2reg_write8`, `case 0x01: // HEN/irq masks`) —
  when an **SH-2** writes adapter-block offset 1 (`$4001` in its own address space), the byte
  means something completely different from what it means when the **68000** writes the *same
  offset* (`$A15101`, nRES/ADEN — already correctly implemented and must stay that way). The
  SH-2's own view is its per-core interrupt-enable register, confirmed bit-for-bit against
  `32x.c:83-84`'s `mask & (sh2irq_mask[core] << 3)` shift lining up exactly with the pending-bit
  table above: bit 0 = PWM enable, bit 1 = CMD enable, bit 2 = HINT enable, **bit 3 = VINT
  enable** (resolved this pass — previously only inferred by elimination). **VRES is applied
  completely outside that masked expression** (`sh2irqi[core] |= mask & P32XI_VRES`, no gating at
  all) — it is never maskable via this register, delivered unconditionally to both cores.
  GenesisSharp's current `Sega32X.WriteControlByteFromSh2` treats offset 1 as "68000-exclusive"
  and silently discards every SH-2 write there — this is the single most concrete blocker, and
  why Phase 1 below starts there.
- **VRES's real relationship to reset, resolved — genuinely two separate concepts on real
  hardware, not one**: `p32x_reset_sh2s` (`32x.c:161-212`, the function tied to the *software*
  nRES 0→1 register edge, which GenesisSharp's `WriteControlByteFrom68k` already correctly
  mirrors with `MasterSh2.Reset()`/`SlaveSh2.Reset()`/`SynthesizeSh2BootStateFromCartridge()`)
  does **not** trigger a VRES interrupt anywhere in its body — confirmed by reading it in full.
  VRES-the-interrupt is exclusively triggered by `PicoReset32x` (`32x.c:240-249`), the
  *whole-system* reset path (power-on / user-initiated reset — GenesisSharp's
  `GenesisConsole.Reset()`), which GenesisSharp has no 32X-facing hook into at all yet. This means
  the existing nRES-toggle handling needs **no changes** — Phase 4 is a pure addition, not a
  modification of already-correct code.
- **Sync-ordering concern, resolved as not applicable**: PicoDrive's `p32x_sync_sh2s` calls
  (guarding both the offset-3 write and the offset-2 read, `memory.c:357,431`) exist because its
  own scheduler lets SH-2 execution drift up to 244 68000-cycles out of sync before forcing a
  catch-up (`CYCLES_GT(cycles - m68krcycles_done, 244)`). GenesisSharp's scanline-stepping model
  already steps all three CPUs within the same per-scanline loop — a strictly tighter
  synchronization granularity than PicoDrive's own 244-cycle threshold — so no equivalent
  mechanism is needed here.

## Phase 0 — Confirm the remaining unread facts — DONE

**Goal**: close every open question before writing SH-2-interrupt-facing code, matching this
project's own standard for citing ground truth rather than guessing at hardware behavior.
Research only, no GenesisSharp code changes. All five questions closed this pass; findings folded
into the Ground Truth section above rather than duplicated here.

1. ~~Exact IRL priority encoding~~ — confirmed: even-spaced levels 6/8/10/12/14, auto-vectors
   67-71, maps directly onto `Sh2.RaiseInterrupt` unchanged.
2. ~~SH-2 mask-register bit 3's meaning~~ — confirmed: VINT enable.
3. ~~VRES's relationship to existing reset handling~~ — confirmed: entirely separate mechanism,
   tied to whole-system reset, not the nRES toggle.
4. ~~68000-side "irq ctl" read path~~ — confirmed: plain bit readback, this is what the live
   stall's polling loop is actually watching clear.
5. ~~Scanline-stepping sync-ordering concern~~ — confirmed not applicable; GenesisSharp's existing
   architecture already provides tighter synchronization than PicoDrive needs its explicit sync
   calls for.

**Verification**: N/A (research only).

## Phase 1 — CMD interrupt + SH-2 IRQ mask register — DONE (confirmed real, correct, and not
## the blocker for the current live stall — see Verification #4 below)

**Goal**: get a 68000-initiated CMD request through to a real SH-2 interrupt, serviced, and
acknowledged back — originally suspected (not yet confirmed at the time this phase started) to be
what the real-ROM stall needed. Turned out not to be that specific stall's blocker (see
Verification #4), but is real, confirmed-correct functionality the game plausibly uses elsewhere,
and was worth building and landing on its own merits regardless.

**What actually landed** (two details turned out different from the original sketch below, both
discovered while implementing and confirmed against source before coding around them):
- The SH-2's acknowledgment is **not** a write back to the same offset 2/3 the 68000 requests
  through — real hardware has a dedicated pending-interrupt-clear register block at offsets
  `0x14/0x16/0x18/0x1a/0x1c` (VRES/VINT/HINT/CMD/PWM respectively), confirmed
  `memory.c:925-983` (`p32x_sh2reg_write16`). CMD's own clear is offset `0x1a`. There is no
  byte-write equivalent on real hardware at all — `p32x_sh2reg_write8` has no case for this
  range, falling through to its own "unhandled sysreg" log line — so real SH-2 code always uses
  a word write here, which `Sega32XSh2Bus.WriteWord` decomposes into two byte writes;
  `Sega32X.WriteRegisterByteFromSh2` matches on `offset & ~1` to treat both bytes of the pair as
  the same register.
- No general "priority-resolve across all five pending sources" bitmask was built this phase —
  only CMD is wired up, and `UpdateCmdIrq` calls `Sh2.RaiseInterrupt` directly (which already only
  raises if the new level is higher than whatever's pending, so re-evaluating on every relevant
  write is safe without an intermediate bitmask). Revisit this once a second source (Phase 2)
  makes "which of several simultaneously-pending sources gets serviced first" a real question —
  not one yet, since nothing else raises an interrupt at all.
- `WriteRegisterByteFromSh2`'s signature gained a `bool isSlave` parameter (previously had no way
  to know which core was writing) — its one real call site, `Sega32XSh2Bus.WriteByte`, updated to
  pass its own `_isSlave` field through.
- `Sh2IrqMask` ended up `public`, not `internal` — matches how `Regs`/`VdpRegs`/`Sdram` are
  already exposed for direct test inspection, and avoids needing `InternalsVisibleTo` (not
  currently configured for this project) just for this one field.

New `src/GenesisSharp.Core/Sega32X.Interrupts.cs` (partial class, matching the existing
per-concern split of `Sega32X.Vdp.cs`/`Sega32X.Pwm.cs`/`Sega32X.BootStub.cs`): `Sh2IrqMask`,
`UpdateCmdIrq`, `AcknowledgeCmdIrq`. `WriteControlByteFrom68k` gained the offset-2/3 CMD-request
write path (`Sega32X.cs`). `ReadControlByteFor68k`/`ReadControlByteForSh2` needed no changes —
already correctly returned the live stored bits by construction, confirmed rather than assumed.

**Verification**:
1. ~~Unit test: SH-2 write to offset 1 sets `Sh2IrqMask`~~ — done,
   `Sega32XInterruptTests.WriteRegisterByteFromSh2_OffsetOne_*` (two tests: sets the writing
   core's own mask without touching `Regs[0]`'s nRES/ADEN view, and doesn't cross-affect the
   other core's mask).
2. ~~A synthetic CMD-IRQ round-trip test~~ — done,
   `CmdInterrupt_68000RequestServicedByMasterSh2_RoundTripsThroughARealInterrupt`: 68000 requests
   CMD, hand-assembled master SH-2 code (CMD unmasked) actually takes a real interrupt at vector
   68, services it, writes a sentinel through COMM1, and acknowledges via offset 0x1a — all
   through real CPU stepping on both sides, confirmed via `RunFrame()`, not a shortcut.
   `CmdInterrupt_RequestedButNotUnmaskedByTheTargetCore_NeverFires` covers the negative case (the
   live-AND is real, not a rubber stamp).
3. ~~`dotnet build`/`dotnet test`~~ — done, full suite green (590/590), zero behavioral change to
   any non-32X ROM.
4. ~~Live re-test~~ — done, and genuinely informative even though the stall didn't clear: hex-
   dumping the 68000 bus at `$A15100` while stuck at the same `COMM0` poll shows `Regs[1]`
   (adapter-block offset 2/3, "irq ctl") reads `0x0000` — **the 68000 never requests a CMD
   interrupt at all for this handshake**. Phase 1's mechanism is real, correct, and covered by the
   round-trip test above, but this specific stall doesn't go through it — confirmed, not assumed,
   before moving on rather than chasing a fix in the wrong phase. The master SH-2's own PC hasn't
   moved in ~44 million cycles (`CMP/EQ R4,R1` at `$06000A4E`, comparing values that never
   change), consistent with it waiting on its own periodic tick (VBLANK) to drive its main loop
   forward rather than an interrupt from the 68000 — i.e. this is Phase 2's problem, exactly as
   this plan's own Phase 2 goal statement anticipated ("very likely the *next* thing Pitfall's
   SH-2 cores wait on"). Phase 1 is complete and correct; it just wasn't the blocker for this
   particular stall.

## Phase 2 — VINT (68000's own VDP timing → SH-2) — DONE, LIVE-VERIFIED. HINT DEFERRED (see below)

**Goal**: real 32X titles commonly synchronize SH-2-side graphics/audio work to the Genesis VDP's
own vblank timing — confirmed to be exactly what Pitfall's SH-2 cores were waiting on (see live
re-test result below).

**HINT scoped out of this phase, not merely unstarted**: PicoDrive's own `p32x_schedule_hint`
(`32x.c:348-364`) calls 32X HINT "rather rough... useless in practice" in its own source comment.
Unlike VINT, HINT isn't a simple hook into an existing event — it needs a genuinely separate
fixed-point scanline-counting timer driven by its own SH-2-side "H count" register (adapter
offset 4/5, independent of the 68000's own VDP register 10) plus a previously-undiscovered global
"HEN" enable bit (adapter-block offset 1, bit 7 — a *different* bit than the four per-core
`Sh2IrqMask` bits this plan already covers, confirmed `memory.c:824-839`'s own `sh2_regs[0] |=
d&0x80` alongside the mask-bit handling). Given PicoDrive's own admitted low confidence and the
live evidence that VINT alone was the actual blocker, HINT is deferred to its own follow-up
rather than building low-confidence timing code now.

**What actually landed** — one real bug found and fixed before it shipped, not assumed away:
`Vdp.VerticalBlankStarted` (the existing 68000/Z80 vblank hook) turned out to be gated by
`VerticalInterruptEnabled` (the 68000's own VDP register 1 IE0 bit) — confirmed by reading
`Vdp.Timing.cs`'s `AdvanceScanline` directly. 32X VINT has no such dependency on real hardware
(`p32x_start_blank`, `32x.c:316-330`, raises VINT with no check on the 68000's own interrupt
enable anywhere in it) — hooking the gated event would have silently never fired for any ROM that
doesn't happen to also enable the 68000's own vblank interrupt at the same point, caught by the
round-trip test failing before it ever reached a live ROM. Fixed by adding a new, deliberately
*unconditional* `Vdp.EnteredVBlank` event fired at the same edge, and hooking that instead.
`GenesisConsole`'s constructor wires `Vdp.EnteredVBlank += Sega32X.OnVerticalBlankStarted`.
`Sega32X.Interrupts.cs` gained `OnVerticalBlankStarted` (VINT = `RaiseInterrupt(12, 70)`, gated
per-core by `VIntMaskBit`, no persistent request state to re-evaluate the way CMD has — a
one-shot edge, not a live AND) and documented no-op handling for offset 0x16 (VINT's own
pending-clear register — harmless in this design since `Sh2.RaiseInterrupt` already self-clears
once serviced, with nothing persistent left to explicitly acknowledge).

**Verification**:
1. ~~Unit tests exercising the new hook directly~~ — done,
   `OnVerticalBlankStarted_CoreWithVIntUnmasked_RaisesInterruptOnThatCoreOnly` and
   `OnVerticalBlankStarted_NeitherCoreHasVIntUnmasked_NeverFires` (`Sega32XInterruptTests.cs`).
2. ~~A round-trip test through a real vblank edge~~ — done,
   `VInt_ARealFramesOwnVBlankEdge_RoundTripsThroughARealInterruptOnTheMaster`: releases the SH-2s,
   lets a single real `RunFrame()` reach its own natural vblank with no test-injected timing, and
   confirms the master's VINT handler actually ran via a sentinel write through COMM2 — this is
   the test that caught the `VerticalBlankStarted`-vs-`EnteredVBlank` gating bug above before it
   ever reached a live ROM.
3. ~~`dotnet build`/`dotnet test`~~ — done, full suite green (593/593), zero behavioral change to
   any non-32X ROM (the new `EnteredVBlank` event is purely additive; nothing else subscribes to
   it).
4. ~~Live re-test~~ — done, and this is the phase that actually cleared the stall: Pitfall now
   boots past the `COMM0` deadlock entirely and renders real gameplay content (confirmed visually
   — Harry in a doorway, a stone-face wall decoration, matching a genuine scene from the game).
   This is the first time in the whole investigation the game has produced real rendered gameplay,
   not just a splash/warning screen. A new, separate visual issue surfaced once past the boot
   stall (flickering/corrupted pixels in a strip of the frame) — not investigated as part of this
   plan; most likely 32X frame-buffer rendering (bank-swap timing or partially-written VRAM), a
   different subsystem than interrupt routing. Worth its own follow-up.

## Phase 3 — PWM interrupt

**Goal**: `Sega32X.Pwm.cs` already computes the FIFO-needs-feeding condition (Phase 4 of the
original 32X plan's own known-gaps note flagged this as "a real, load-bearing gap for some real
games") — this phase wires that already-existing signal into the interrupt path built in Phase 1
rather than leaving it a dead end. Confirmed trigger: `do_pwm_irq`/`pwm.c:49-52`, PWM =
`RaiseInterrupt(6, 67)`.

**Verification**: unit test confirming the FIFO-empty condition now raises a mask-gated pending
interrupt; full suite green; live re-test (lower confidence this is what Pitfall's current stall
specifically needs, but confirms no regression to Phase 4's existing PWM audio behavior).

## Phase 4 — VRES

**Goal**: close out the fifth and last source. Per the Ground Truth section above, this is a pure
addition, not a modification — the existing nRES-edge CPU-reset handling is confirmed correct
and untouched. Add a new call from `GenesisConsole.Reset()` (the whole-system reset path,
`PicoReset32x`'s equivalent) that raises VRES on both cores unconditionally
(`RaiseInterrupt(14, 71)` on each, unmasked — no `Sh2IrqMask` gating, confirmed above).

**Verification**: unit test confirming `GenesisConsole.Reset()` raises VRES on both SH-2 cores
regardless of their own `Sh2IrqMask` state; full suite green.

## Explicitly out of scope for this plan

- DREQ/DMAC-triggered interrupts, if any exist beyond the five sources above — not investigated;
  `pico_int.h:624-628` is believed complete but that's the extent of the confirmation so far.
- Cycle-accurate interrupt latency/timing — every other 32X phase in this project has treated
  exact timing as a later-refinement concern where PicoDrive itself admits uncertainty (HBLK/nFEN,
  autofill burst timing, DIVU/DIVS cycle counts); this plan follows the same pattern unless a real
  ROM's own behavior demands otherwise.

## Overall verification (every phase)

1. `dotnet build GenesisSharp.sln` — 0 warnings/errors.
2. `dotnet test GenesisSharp.sln` — full suite green, growing with each phase's new tests.
3. A SagaRoms smoke-test run on existing non-32X titles wherever a phase touches shared
   `GenesisConsole`/`Vdp` code (Phase 2 specifically), to catch collateral damage — the same bar
   Phase 2 of the original 32X bus-integration work was held to.
4. `ARCHITECTURE.md` gets a new subsection once this plan is fully implemented, following the
   existing per-phase documentation and confidence-language conventions exactly — this document
   folds into that write-up rather than staying a separate standing file afterward.
