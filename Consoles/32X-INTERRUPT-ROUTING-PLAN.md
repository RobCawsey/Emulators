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

## Ground truth used across every phase (confirmed against `reference/PicoDrive/picodrive/pico/32x/`)

- **All five interrupt sources are SH-2-side only.** Per `pico_int.h:624-628`:
  `P32XI_VRES`/`P32XI_VINT`/`P32XI_HINT`/`P32XI_CMD`/`P32XI_PWM`. Searched exhaustively: no
  `SekInterrupt`/similar call site anywhere in `32x.c`/`memory.c`/`pwm.c` raises an interrupt on
  the 68000 itself from a 32X event. The 68000 is expected to be notified of SH-2 activity by
  **polling** COMM registers, not by receiving its own 32X-sourced interrupt — matching exactly
  what the live trace shows (it's the 68000 stuck polling, the SH-2s that need waking).
- **A confirmed, concrete gap in GenesisSharp's own current source** (read directly this
  session): `Sega32X.WriteControlByteFromSh2` treats adapter-block offset 1 as "68000-exclusive"
  and silently discards every SH-2 write there. Real hardware disagrees — confirmed
  `memory.c:824-839` (`p32x_sh2reg_write8`, `case 0x01: // HEN/irq masks`): the *same offset*
  means nRES/ADEN when the 68000 writes it, but means the SH-2's own per-core interrupt-enable
  register when an SH-2 writes it (bit 0 = PWM, bit 1 = CMD, bit 2 = HINT, bit 3 = unconfirmed,
  likely VINT by elimination). This asymmetry is the single most concrete blocker and is why
  Phase 1 below starts there.
- **Priority resolution** (`p32x_update_irls`, `32x.c:40-74`, only partially read so far): the
  five pending bits fold into one IRL value per core via what looks like a priority encoder, fed
  to `sh2_irl_irq`. Not fully confirmed yet — Phase 0 below reads this in full before any phase
  that needs it.

## Phase 0 — Confirm the remaining unread facts

**Goal**: close every gap flagged "unconfirmed" below before writing SH-2-interrupt-facing code,
matching this project's own standard for citing ground truth rather than guessing at hardware
behavior. Research only, no GenesisSharp code changes.

1. Read `p32x_update_irls` (`32x.c:40-74`) in full — the exact bitmask-to-IRL priority
   encoding, and confirm it maps cleanly onto `Sh2.RaiseInterrupt(level, vectorNumber)`'s existing
   shape (built in Phase 1 of the original SH-2 core plan, unused for 32X so far) rather than
   needing a different interrupt-delivery shape on the `Sh2` core.
2. Confirm SH-2 mask-register bit 3's meaning directly (currently inferred by elimination against
   the other four named bits, not read from source).
3. Confirm VRES's exact relationship to the existing reset handling: does real hardware raise a
   literal VRES *interrupt* in addition to resetting the SH-2 core, or is GenesisSharp's existing
   `MasterSh2.Reset()`/`SlaveSh2.Reset()` on the nRES 0→1 edge already the complete, correct
   behavior with nothing further needed?
4. Read the 68000-side "irq ctl" register's *read* path (not just the write path already found at
   `memory.c:429-436`) — confirm what a 68000-side readback of `$A15102`/`$A15103` is supposed to
   report, since the current stall's polling loop may end up depending on it once Phase 1 lands.
5. Confirm whether GenesisSharp's existing scanline-stepping model (all three CPUs stepped within
   the same per-scanline loop, unlike PicoDrive's more decoupled scheduler) needs an equivalent to
   `p32x_sync_sh2s`'s ordering requirement before an interrupt request is evaluated, or whether
   that concern simply doesn't apply here.

**Verification**: none (research only) — each finding gets folded into the phase below it
actually gates, cited the same way every other fact in this file is.

## Phase 1 — CMD interrupt + SH-2 IRQ mask register (unblocks the current live stall)

**Goal**: the specific mechanism directly implicated by the real-ROM stall this investigation
found — get a 68000-initiated CMD request through to a real SH-2 interrupt, serviced, and
acknowledged back. Smallest, most concretely-understood piece of this whole plan, and the one
with a real ROM already sitting at the exact point it would exercise.

- **New `Sega32X.Interrupts.cs`** (partial class, matching the existing per-concern split of
  `Sega32X.Vdp.cs`/`Sega32X.Pwm.cs`/`Sega32X.BootStub.cs`): `byte[2] Sh2IrqMask` (per-core, low 4
  bits meaningful), per-core pending-interrupt state, and the raise-into-`Sh2.RaiseInterrupt` step
  using Phase 0's confirmed priority encoding.
- **`Sega32X.WriteControlByteFromSh2`**: stop discarding offset-1 writes — route to the new mask
  register instead, confirmed against `memory.c:824-839`.
- **`Sega32X.WriteControlByteFrom68k`**: add the CMD-request write path for adapter-block offset
  2/3 ("irq ctl", currently unhandled), confirmed against `memory.c:429-436` — bits 0/1 =
  request-to-master/request-to-slave, gated live against each core's own mask bit
  (`p32x_update_cmd_irq`, `32x.c:89-102`), not a one-shot latch.
- **SH-2 acknowledgment**: confirm/implement the SH-2-side write that clears its own request bit
  back (`memory.c:946`) — this is what the 68000's polling loop is actually waiting to observe.

**Verification**:
1. Unit test: SH-2 write to offset 1 sets the mask register; a 68000 write to the *same offset*
   is unaffected and still sets nRES/ADEN correctly (proves the asymmetry is real, not a
   regression of already-working behavior).
2. A synthetic CMD-IRQ round-trip test, mirroring Phase 2 of the original 32X plan's own
   `GenesisConsoleSh2BusIntegrationTests.cs` shape: 68000 requests a CMD IRQ, hand-assembled SH-2
   code with CMD unmasked services it and writes an acknowledgment, 68000-side test code confirms
   it — proves the plumbing end-to-end without needing the real commercial ROM.
3. `dotnet build`/`dotnet test` — full suite green, zero behavioral change for any non-32X ROM.
4. **Live re-test**: reload Pitfall: The Mayan Adventure with the same breakpoint/live-trace
   methodology this session established, and confirm the 68000's `COMM0` poll actually clears —
   the direct, concrete pass/fail signal for this phase specifically.

## Phase 2 — VINT/HINT (68000's own VDP timing → SH-2)

**Goal**: real 32X titles commonly synchronize SH-2-side graphics/audio work to the Genesis VDP's
own vblank/hblank timing — very likely the *next* thing Pitfall's SH-2 cores wait on once Phase 1
unblocks the current stall, based on the pattern already seen (both cores actively running but
not progressing toward anything COMM-related).

- New `GenesisConsole` → `Sega32X` hook fired at the same vblank/hblank edges the existing
  68000-side VDP interrupt flags already use (`Vdp.cs` already owns this timing; this phase adds
  a call-out, not new timing logic).
- Route through `Sega32X.Interrupts.cs`'s mask-gated raise path from Phase 1 — `P32XI_VINT`/
  `P32XI_HINT`, confirmed call sites `32x.c:327`/`32x.c:376` (exact upstream trigger points in
  PicoDrive's own VDP code not traced this pass; GenesisSharp's own `Vdp.cs` vblank/hblank
  detection is the source of truth to hook here, not a port of PicoDrive's own call sites).

**Verification**: unit tests exercising the new hook directly (drive `Vdp` through a vblank/hblank
transition, confirm the SH-2 pending state updates correctly when unmasked and stays clear when
masked); full suite green; live re-test continuing from wherever Phase 1's live test left off.

## Phase 3 — PWM interrupt

**Goal**: `Sega32X.Pwm.cs` already computes the FIFO-needs-feeding condition (Phase 4 of the
original 32X plan's own known-gaps note flagged this as "a real, load-bearing gap for some real
games") — this phase wires that already-existing signal into the interrupt path built in Phase 1
rather than leaving it a dead end. Confirmed trigger: `do_pwm_irq`/`pwm.c:49-52`.

**Verification**: unit test confirming the FIFO-empty condition now raises a mask-gated pending
interrupt; full suite green; live re-test (lower confidence this is what Pitfall's current stall
specifically needs, but confirms no regression to Phase 4's existing PWM audio behavior).

## Phase 4 — VRES

**Goal**: close out the fifth and last source. Depends entirely on Phase 0's finding — either
confirm GenesisSharp's existing nRES-edge `Reset()` calls are already sufficient real-hardware
behavior (likely, but not yet confirmed), or add the literal interrupt raise alongside them if
real hardware does both.

**Verification**: unit test covering whichever behavior Phase 0 confirms; full suite green.

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
