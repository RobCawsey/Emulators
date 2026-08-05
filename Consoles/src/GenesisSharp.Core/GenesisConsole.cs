using System.Collections.Concurrent;
using GenesisSharp.Cpu68000;
using GenesisSharp.CpuZ80;

namespace GenesisSharp.Core;

/// <summary>Top-level system: owns both CPUs, work RAM, the cartridge, the VDP, and the two
/// sound chips, and wires up the per-CPU buses. The Z80's entire memory map is routed: its own
/// 8KB work RAM (mirrored across 0x0000-0x3FFF), the YM2612 (0x4000-0x5FFF), the bank register
/// (0x6000-0x6FFF), the PSG (0x7F00-0x7FFF), and its banked 32KB window into the 68000's
/// address space (0x8000-0xFFFF). The reverse direction is routed too: the 68000 can reach the
/// Z80's own address space directly at 0xA00000-0xA0FFFF (gated by the bus-request register at
/// 0xA11100 — real hardware requires requesting the bus first to avoid contention with
/// whatever the Z80 itself is doing), and hold the Z80 in reset via 0xA11200. The version
/// register and the three I/O ports (0xA10000-0xA1000D) are routed to <see
/// cref="ControllerPort1"/>/<see cref="ControllerPort2"/>/<see cref="ExtPort"/>; their serial
/// Tx/Rx/S-ctrl registers (0xA1000E-0xA1001F) aren't modeled (accepted without effect —
/// Genesis software essentially never uses serial mode). Any 68000 address this class doesn't
/// otherwise recognize — not just that unused I/O corner — reads as open bus and ignores
/// writes rather than erroring (see <see cref="OpenBusReadFallback"/>). The TMSS register (0xA14000) is
/// just a plain read/write latch here: writing "SEGA" to it is what real hardware requires
/// before the VDP responds normally, but that lock isn't enforced — this emulator's VDP is
/// always active regardless of what's written there. Writes to cartridge ROM space
/// (0x000000-0x3FFFFF) are silently dropped, matching real hardware's read-only wiring —
/// this matters more than it might sound: an interrupt firing while the stack pointer is
/// uninitialized or garbage often lands a push right in ROM space.</summary>
public sealed partial class GenesisConsole : Cpu68000.IBus, CpuZ80.IBus
{
    /// <summary>NTSC Genesis timing: a scanline is ~3420 master-clock cycles (53.693175 MHz).
    /// The 68000 runs at master/7, the Z80 at master/15, giving ~488 68000 cycles and ~228 Z80
    /// cycles per scanline — the same ~488 figure <see cref="Vdp.AdvanceScanline"/>'s doc
    /// comment already anticipated. Not true dot-clock-cycle-exact — <see cref="Vdp.ScanlineProgress"/>
    /// only updates once per 68000 instruction, not once per pixel — but it's enough to keep
    /// both CPUs and the VDP advancing in lockstep, one frame at a time, with a reasonable
    /// approximation of where within each scanline the CPU currently is.</summary>
    public const int CyclesPerScanlineM68000 = 488;
    public const int CyclesPerScanlineZ80 = 228;

    public const int AudioSampleRate = 44100;

    // The 68000's own clock -- also the YM2612's, since they share it on real hardware (see
    // Ym2612.Synthesis.cs's own copy of this same figure).
    private const double M68kClockHz = 7_670_454.0;

    // ~60ms worth (3x the frontend's ~20ms WasapiOut latency, same safety-margin ratio as
    // before) -- a real bound on worst-case audio latency, not just a memory backstop. This
    // queue is produced by the emulation thread's own Stopwatch-paced clock and drained by
    // WasapiOut's independent audio-hardware clock; those two clocks aren't the same clock, so
    // even a tiny persistent mismatch between them (ordinary scheduling jitter, not a stall) lets
    // a backlog creep upward over a play session with nothing to drain it back down -- the old
    // ~2-second cap let that backlog grow large enough to become an audible, noticeable delay
    // between a game action (e.g. Sonic's jump) and its sound before ever trimming anything.
    // Capped tighter here so any such creep is inaudible long before it matters.
    private const int MaxBufferedAudioSamples = AudioSampleRate * 3 / 50;

    private readonly byte[] _workRam = new byte[0x10000];
    private readonly byte[] _z80Ram = new byte[0x2000];

    private int _cpuCycleDebt;
    private int _z80CycleDebt;

    /// <summary>SH-2:68000 clock ratio: textbook ~3x, the same approximation already decided
    /// during this project's 32X planning (not independently re-derived here) — see
    /// ARCHITECTURE.md §4a. Applied per-68000-instruction (see <see cref="RunScanline"/>'s own
    /// remarks on why this is no longer a single per-scanline lump sum) rather than as a fixed
    /// <c>CyclesPerScanlineM68000 * 3</c> total, though the two are equivalent in aggregate.</summary>
    private const int Sh2ToM68kClockRatio = 3;

    private int _msh2CycleDebt;
    private int _ssh2CycleDebt;

    private double _audioSampleDebt;

    // Produced by RunScanline (the emulation thread), drained by a frontend's audio callback
    // (a different thread) -- see GenerateAudioSample's remarks for why this exists instead of
    // calling that method directly from the audio thread.
    private readonly ConcurrentQueue<(short Left, short Right)> _audioBuffer = new();

    /// <summary>The Z80's bank register: 9 bits, shifted in one bit at a time (LSB first) by
    /// successive writes to 0x6000-0x6FFF. Left-shifted by 15 (each page of the banked window
    /// is 32KB), 9 bits reaches every 32KB page across the 68000's full 16MB address space.</summary>
    private ushort _z80BankRegister;

    /// <summary>Set by writing bit 0 (byte access) or bit 8 (word access) of 0xA11100. While
    /// true, the Z80 core doesn't execute — its clock is genuinely stopped on real hardware,
    /// not just gated, so cycles don't accumulate as debt while held either (see
    /// <see cref="RunScanline"/>). No arbitration delay is modeled: the grant is immediate, so
    /// the classic "request then poll until granted" idiom
    /// (<c>move.w #$100,($A11100); btst #0,($A11101); bne ...</c>) falls through on its very
    /// first check.</summary>
    private bool _z80BusRequested;

    /// <summary>Set by writing bit 0 (byte) / bit 8 (word) of 0xA11200 to *clear* (0 = held in
    /// reset, 1 = released) — the Z80's registers are cleared at the moment reset is asserted,
    /// matching the real boot sequence: request the bus, hold reset, load the sound driver into
    /// Z80 RAM through the window below, release reset, release the bus.</summary>
    private bool _z80Held;

    /// <summary>Guards against a pathological bank-register value that points the Z80's own
    /// 0x8000-0xFFFF window back at its own 0xA00000-0xA0FFFF address on the 68000 side (or
    /// vice versa), which would otherwise recurse forever. One round-trip is allowed in each
    /// direction — enough for every real use — and anything past that reads back as open bus
    /// instead of overflowing the stack. Real hardware's answer to the same setup is bus
    /// contention/undefined values, so this isn't a meaningfully different outcome.</summary>
    private bool _crossBusAccessInProgress;

    private readonly byte[] _tmssRegister = new byte[4];

    /// <summary>Bit 7: 0 = domestic (Japan), 1 = overseas. Bit 6: 0 = NTSC, 1 = PAL. Bit 5:
    /// 0 = expansion (Mega CD) port present, 1 = not present. Bits 0-3: hardware revision. Bit
    /// 6's polarity is confirmed correct at 0 = NTSC: it matches genesis-plus-gx's own
    /// <c>REGION_USA</c>-derived value independently, and a real 32X title's live boot code
    /// (Pitfall: The Mayan Adventure) confirms it two different ways once <see
    /// cref="Sega32X.Vdp.cs"/>'s own <c>NPalBit</c> default is also correct (see that constant's
    /// remarks) — the same title's region/hardware sanity check and a second, separate 2-bit
    /// region-code dispatch (bits 6-7 read as a region value: 2 = USA) both pass cleanly with
    /// this value and the corrected <c>nPAL</c> default together. A previous revision of this
    /// value flipped bit 6 to 1 based on the first check alone, before the <c>nPAL</c> polarity
    /// bug (the real root cause) was found — that flip happened to satisfy the first check but
    /// broke the second, sending this exact title into its own "NTSC GENESIS SYSTEMS" region-
    /// mismatch warning screen followed by a deliberate halt, which is what a real region
    /// mismatch does on real hardware, not a workaround. Reverted once both checks were traced
    /// live and shown to require the *original* polarity once the real bug was fixed. A settable
    /// property (not a const) so <see cref="GenesisSharp.Frontend.DebugForm"/> can still override
    /// this live for further experimentation without a rebuild.</summary>
    public byte VersionRegisterValue { get; set; } = 0xA0;

    public Cartridge Cartridge { get; }
    public M68000 Cpu { get; }
    public Z80 SoundCpu { get; }
    public Vdp Vdp { get; }
    public Ym2612 Ym2612 { get; } = new();
    public Psg Psg { get; } = new();
    public ControllerPort ControllerPort1 { get; } = new();
    public ControllerPort ControllerPort2 { get; } = new();

    /// <summary>The EXT port — no standard controller ever plugs in here, so its pad defaults
    /// to disconnected (all data lines simply read high) rather than the two main ports'
    /// "a 3-button pad is plugged in, nothing pressed" default.</summary>
    public ControllerPort ExtPort { get; } = new();

    /// <summary>Phase 1 of an in-progress Sega 32X extension (see ARCHITECTURE.md §4a) — always
    /// constructed, like every other chip here, but inert for any non-32X ROM: its two SH-2s are
    /// never stepped (see <see cref="RunScanline"/>) unless a ROM's own boot code sets the ADEN
    /// bit itself.</summary>
    public Sega32X Sega32X { get; }

    public GenesisConsole(Cartridge cartridge)
    {
        Cartridge = cartridge;
        Cpu = new M68000(this);
        SoundCpu = new Z80(this);
        Vdp = new Vdp { ExternalMemoryRead = ReadExternalMemoryByte };
        Vdp.VerticalBlankStarted += RequestVerticalBlankInterrupt;
        Vdp.HorizontalInterruptRequested += RequestHorizontalInterrupt;
        ExtPort.Pad.Connected = false;
        Sega32X = new Sega32X(cartridge);
        Vdp.External32XPixelBlend = Sega32X.TryGetPixel;
    }

    public void Reset()
    {
        Cpu.Reset();
        SoundCpu.Reset();
        Vdp.Reset();
        Ym2612.Reset();
        Psg.Reset();
        ControllerPort1.Reset();
        ControllerPort2.Reset();
        ExtPort.Reset();
        Sega32X.Reset();
        Array.Clear(_tmssRegister);
        _cpuCycleDebt = 0;
        _z80CycleDebt = 0;
        _msh2CycleDebt = 0;
        _ssh2CycleDebt = 0;
        _audioSampleDebt = 0;
        _audioBuffer.Clear();
        AudioUnderrunCount = 0;
        AudioClipCount = 0;
        _z80BankRegister = 0;
        _z80BusRequested = false;
        _z80Held = false;
        _crossBusAccessInProgress = false;
    }

    /// <summary>Runs one full frame: interleaves the 68000 and Z80 against their per-scanline
    /// cycle budgets, rendering each active scanline into the VDP's frame buffer and advancing
    /// VDP timing — which is what fires the vblank interrupt into both CPUs at the right moment
    /// (see <see cref="RequestVerticalBlankInterrupt"/>). A frontend calls this once per host
    /// frame to both produce a picture (<see cref="Vdp.FrameBuffer"/>) and keep the emulated
    /// system's clock moving. Call <see cref="Reset"/> first.</summary>
    public void RunFrame()
    {
        for (int i = 0; i < Vdp.LinesPerFrame; i++)
        {
            RunScanline();
        }
    }

    /// <summary>Each CPU accumulates a per-scanline cycle debt and runs instructions until
    /// it's paid off — instructions rarely divide evenly into the budget, so any overshoot
    /// carries into the next scanline's debt rather than being discarded, keeping the long-run
    /// average cycle rate accurate even though any single scanline's count can be a little
    /// off. <see cref="Vdp.AdvanceExternalSlotClock"/>/<see cref="Vdp.ConsumeStallCycles"/> feed
    /// VDP FIFO/DMA stall cycles into that same debt, so a CPU that just triggered a big DMA (or
    /// overran the 4-entry FIFO) genuinely stops running for a while — the debt goes deeply
    /// negative and <c>_cpuCycleDebt</c> simply doesn't cross back above zero for however many
    /// scanlines that takes, exactly like the real 68000 being frozen by VDP bus contention.</summary>
    private void RunScanline()
    {
        // Drives the 32X VDP's VBLK/PEN status bits and applies any deferred frame-buffer bank
        // swap -- run before this scanline's render so a swap landing exactly on the
        // blanking-to-active edge is already committed by the time the first active line reads
        // from the (possibly just-swapped) display bank.
        Sega32X.UpdateBlankingState(Vdp.CurrentScanline >= Vdp.ScreenHeight);

        if (Vdp.CurrentScanline < Vdp.ScreenHeight)
        {
            Vdp.RenderScanline(Vdp.CurrentScanline);
        }

        _cpuCycleDebt += CyclesPerScanlineM68000;
        while (_cpuCycleDebt > 0)
        {
            Vdp.SetScanlineProgress(1.0 - (double)_cpuCycleDebt / CyclesPerScanlineM68000);
            Vdp.AdvanceExternalSlotClock(Cpu.TotalCycles);
            int cpuCycles = Cpu.Step();
            _cpuCycleDebt -= cpuCycles;
            _cpuCycleDebt -= Vdp.ConsumeStallCycles();

            // Interleaved at 68000-instruction granularity rather than checked once at the end
            // of the scanline (this loop's own -- since fixed -- shape through Phase 5): real
            // 32X hardware runs the 68000 and both SH-2s genuinely concurrently, so a 68000
            // boot-code loop that toggles nRES/ADEN faster than one scanline's worth of 68000
            // cycles (confirmed happening in a real, commercial 32X title -- see ARCHITECTURE.md
            // §4a.5) must still see the SH-2s get *some* cycles between each of its own checks.
            // Checking Sega32X.Sh2sReleased only once per scanline let a fast-enough retry loop
            // permanently starve the SH-2s -- by the time this method got around to stepping
            // them, the 68000 had already toggled the release condition back off within that
            // same scanline, every single scanline, forever. Total SH-2 cycles accrued per
            // scanline is unchanged (summing cpuCycles*Sh2ToM68kClockRatio across every
            // iteration this scanline still totals CyclesPerScanlineM68000*Sh2ToM68kClockRatio,
            // the same fixed ~3x ratio as before) -- only the granularity at which that budget
            // is handed out changed.
            if (Sega32X.Sh2sReleased)
            {
                _msh2CycleDebt += cpuCycles * Sh2ToM68kClockRatio;
                while (_msh2CycleDebt > 0)
                {
                    _msh2CycleDebt -= Sega32X.MasterSh2.Step();
                }

                _ssh2CycleDebt += cpuCycles * Sh2ToM68kClockRatio;
                while (_ssh2CycleDebt > 0)
                {
                    _ssh2CycleDebt -= Sega32X.SlaveSh2.Step();
                }
            }
        }

        // Held or bus-requested: the Z80's clock is genuinely stopped on real hardware, so it
        // neither runs nor accrues cycle debt to "catch up" on later.
        if (!_z80BusRequested && !_z80Held)
        {
            _z80CycleDebt += CyclesPerScanlineZ80;
            while (_z80CycleDebt > 0)
            {
                _z80CycleDebt -= SoundCpu.Step();
            }
        }

        // Unlike the Z80 CPU itself, the YM2612's own clock (and so its Timer A/B) keeps
        // running regardless of Z80 bus arbitration -- it's a separate chip sharing a clock
        // domain, not something the Z80 bus-request line freezes. Driven by a fixed
        // per-scanline amount rather than however many cycles the Z80 loop above actually
        // consumed, matching how CyclesPerScanlineZ80 is already treated as that clock
        // domain's nominal per-scanline budget elsewhere in this method.
        Ym2612.AdvanceTimers(CyclesPerScanlineZ80);

        // Cycle-accurate audio: generate samples here, right after both CPUs have run their
        // share of this scanline, instead of a frontend's audio callback pulling them from a
        // separate thread whenever its buffer wants more (see GenerateAudioSample's remarks).
        // Accumulated in "samples worth" per scanline (real time per scanline is
        // CyclesPerScanlineM68000 / M68kClockHz seconds) rather than tracking either CPU's
        // actual consumed cycles, matching how CyclesPerScanlineZ80 is already treated as a
        // fixed nominal budget above -- this also sidesteps needing to reconcile the two CPUs'
        // independently-varying actual cycle counts into one shared audio clock.
        _audioSampleDebt += AudioSampleRate * (CyclesPerScanlineM68000 / M68kClockHz);
        while (_audioSampleDebt >= 1.0)
        {
            _audioSampleDebt -= 1.0;
            if (_audioBuffer.Count >= MaxBufferedAudioSamples)
            {
                _audioBuffer.TryDequeue(out _); // prefer fresher audio over an ever-growing backlog
            }

            _audioBuffer.Enqueue(GenerateAudioSample());
        }

        Vdp.AdvanceScanline();
    }

    /// <summary>Builds one stereo sample from the chips' *current* register state, mixing the
    /// PSG (mono, duplicated to both channels), the YM2612 (natively stereo via its own L/R
    /// enable bits), and the 32X's PWM chip (see <see cref="Sega32X.GeneratePwmSample"/> —
    /// silent/zero for any non-32X ROM, since <c>xMd</c>'s power-on default is one of PWM's own
    /// confirmed-invalid routing values). This is the low-level building block <see
    /// cref="RunScanline"/> calls at cycle-accurate intervals to fill
    /// <see cref="TryDequeueBufferedAudioSample"/>'s queue — a real frontend should pull from
    /// that queue, not call this directly, since doing so would advance the chips' internal
    /// oscillator phase a second, unsynchronized time. Public (and self-contained: no CPU
    /// stepping needed) so it's still directly usable for isolated chip-synthesis testing without
    /// going through a whole frame.</summary>
    public (short Left, short Right) GenerateAudioSample()
    {
        short psgSample = Psg.GenerateSample(AudioSampleRate);
        (short ymLeft, short ymRight) = Ym2612.GenerateSample(AudioSampleRate);
        (short pwmLeft, short pwmRight) = Sega32X.GeneratePwmSample(1.0 / AudioSampleRate);

        int rawLeft = psgSample + ymLeft + pwmLeft;
        int rawRight = psgSample + ymRight + pwmRight;
        if (rawLeft is < short.MinValue or > short.MaxValue || rawRight is < short.MinValue or > short.MaxValue)
        {
            AudioClipCount++;
        }

        return (
            (short)Math.Clamp(rawLeft, short.MinValue, short.MaxValue),
            (short)Math.Clamp(rawRight, short.MinValue, short.MaxValue));
    }

    /// <summary>How many samples <see cref="GenerateAudioSample"/> has had to hard-clip (PSG +
    /// YM2612's combined amplitude exceeding a 16-bit sample's range) since the last <see
    /// cref="Reset"/> -- a live diagnostic for whether hard clipping, not a gap in the audio
    /// buffer, is what's actually producing an audible crackle. Frequent clipping sounds like
    /// harsh distortion rather than a click, but on short, loud, percussive sounds specifically
    /// (the kind most likely to push several channels toward full amplitude at once) that
    /// distortion is exactly what "crackly" or "not crisp" describes.</summary>
    public long AudioClipCount { get; private set; }

    /// <summary>What a real frontend's audio callback should call instead of
    /// <see cref="GenerateAudioSample"/> directly: pulls one sample that <see
    /// cref="RunScanline"/> already generated at the correct point in emulated time, rather
    /// than generating one on demand whenever the callback happens to run (which is what this
    /// emulator used to do, and which meant a callback landing between two register writes that
    /// were only ever meant to be seen together — e.g. a channel's frequency, latched across
    /// two separate byte writes — could catch a half-updated state; more generally, any register
    /// write's effect on a short percussive sound could land up to a whole video frame off from
    /// when it actually happened, in either direction). Returns false — and silence — if the
    /// buffer's empty, typically because emulation is paused or hasn't produced a frame yet,
    /// rather than blocking the audio thread waiting for one; see <see
    /// cref="AudioUnderrunCount"/> if that's happening more than expected.</summary>
    public bool TryDequeueBufferedAudioSample(out short left, out short right)
    {
        if (_audioBuffer.TryDequeue(out var sample))
        {
            (left, right) = sample;
            return true;
        }

        AudioUnderrunCount++;
        left = right = 0;
        return false;
    }

    /// <summary>How many times <see cref="TryDequeueBufferedAudioSample"/> found the buffer
    /// empty and returned silence instead of a real sample -- a live diagnostic for whether the
    /// producer (this class's own <see cref="RunScanline"/>, driven by whatever thread calls
    /// <see cref="RunFrame"/>) is actually keeping up with the consumer (a frontend's real-time
    /// audio callback) in practice. A handful right at startup (before the first frame has run)
    /// is normal; a count that climbs steadily during normal play means the producer thread is
    /// falling behind real time -- each such gap is a moment of true silence, which is exactly
    /// what a click/crackle sounds like.</summary>
    public long AudioUnderrunCount { get; private set; }

    /// <summary>What the VDP fires once per frame at the start of vertical blank. On real
    /// Genesis hardware this feeds both CPUs, but not identically: the 68000 sees a level-6 IPL
    /// request (autovectored to address 0x78 — a separate line/vector from <see
    /// cref="RequestHorizontalInterrupt"/>'s level 4), while the Z80's single INT line carries
    /// only this one signal (which is how the sound driver knows to advance its sequencer).
    ///
    /// Found via real-ROM testing: a homebrew game (Crazy Driver) hung forever polling a RAM
    /// counter that only its level-6 handler incremented, while this code was requesting level
    /// 4 for both interrupts — so the ROM's real vblank handler never ran.</summary>
    public void RequestVerticalBlankInterrupt()
    {
        Cpu.RaiseInterrupt(6);
        SoundCpu.RaiseMaskableInterrupt();
    }

    /// <summary>What the VDP fires every (register 10 + 1) active scanlines when H-interrupts
    /// are enabled — the 68000's level-4 IPL line (autovectored to address 0x70), distinct from
    /// <see cref="RequestVerticalBlankInterrupt"/>'s level 6. Unlike vblank this one doesn't
    /// reach the Z80 (line-based raster effects have no meaning to the sound driver).</summary>
    public void RequestHorizontalInterrupt()
    {
        Cpu.RaiseInterrupt(4);
    }

    /// <summary>Cartridge ROM + 68000 work RAM only — enough for VDP DMA sources, which in
    /// practice only ever come from one of those two places. Everything else (Z80 window, I/O,
    /// unmapped space) reads as open bus (0xFF).
    ///
    /// The DMA source registers can encode the real address's bit 23 directly (see <see
    /// cref="Vdp.DmaSourceAddress"/>'s remarks) -- but plenty of real code, including SGDK's own
    /// output, instead programs the source registers with that bit deliberately stripped --
    /// $FF8BAC becomes $7F8BAC -- because real hardware's RAM chip-select recognizes that pattern
    /// as RAM too. So $7F0000-$7FFFFF aliases back to work RAM here as a second, narrower alias,
    /// alongside the same $E00000-$FFFFFF mirror <see cref="Cpu68000.IBus.ReadByte"/> already
    /// implements for the 68000's own bus (real hardware's RAM chip-select decodes only address
    /// bits A21-A23, ignoring the rest, regardless of whether the CPU or the VDP's own DMA
    /// controller issues the read) -- not just the literal $FF0000-$FFFFFF range once assumed
    /// here, which is what left a real ROM's own $FFxxxx-sourced DMA (Altered Beast, reconstructing
    /// e.g. $FEF000 rather than $FFF000 depending on which bit of the source buffer's address a
    /// given transfer's auto-increment landed on) reading open bus instead of its real source.</summary>
    private byte ReadExternalMemoryByte(uint address)
    {
        if (address < 0x400000 && Cartridge.Rom.Length > 0)
        {
            return Cartridge.Rom[address % (uint)Cartridge.Rom.Length];
        }

        if (address >= 0xE00000 || (address >= 0x7F0000 && address < 0x800000))
        {
            return _workRam[address & 0xFFFF];
        }

        return 0xFF;
    }

    private static bool IsVdpPort(uint address, out uint portOffset)
    {
        portOffset = address & 0x1F;
        return address >= 0xC00000 && address < 0xC00020;
    }

    private static bool IsZ80Window(uint address) => address is >= 0xA00000 and <= 0xA0FFFF;
    private static bool IsZ80BusRequestRegister(uint address) => address is 0xA11100 or 0xA11101;
    private static bool IsZ80ResetRegister(uint address) => address is 0xA11200 or 0xA11201;

    private static bool IsControllerRegister(uint address, out uint offset)
    {
        offset = address - 0xA10000;
        return address >= 0xA10000 && address <= 0xA1001F;
    }

    private static bool IsTmssRegister(uint address, out uint offset)
    {
        offset = address - 0xA14000;
        return address >= 0xA14000 && address <= 0xA14003;
    }

    /// <summary>The 32X adapter/control register block ($A15100-$A1513F) — confirmed against
    /// PicoDrive's own author comment (<c>reference/PicoDrive/picodrive/pico/32x/memory.c:9-25</c>)
    /// and <c>Pico32x.regs[0x20]</c> (<c>pico_int.h:644</c>). See <see cref="Sega32X"/> and
    /// ARCHITECTURE.md §4a for what's actually implemented behind it this phase.</summary>
    private static bool Is32XRegister(uint address, out uint offset)
    {
        offset = address - 0xA15100;
        return address >= 0xA15100 && address <= 0xA1513F;
    }

    /// <summary>"MARS" — the 32X hardware-presence probe real 32X boot code reads before
    /// switching into 32X mode (confirmed against PicoDrive's <c>memory.c:1021-1024</c> etc.).
    /// Always live, matching how PicoDrive's own pre-ADEN register handling is unconditional too
    /// (see Sega32X's known-gaps remarks) — no cartridge-header-based 32X detection is added.</summary>
    private static readonly byte[] MarsId = "MARS"u8.ToArray();

    private static bool IsMarsIdRegister(uint address, out uint offset)
    {
        offset = address - 0xA130EC;
        return address >= 0xA130EC && address <= 0xA130EF;
    }

    /// <summary>Phase 3 (see ARCHITECTURE.md §4a.1) — the 32X VDP's display-mode/PRI/SFT/fill/FBCR
    /// register block ($A15180-$A1519F), confirmed against <c>Pico32x.vdp_regs[0x10]</c>
    /// (<c>pico_int.h:645</c>).</summary>
    private static bool Is32XVdpRegister(uint address, out uint offset)
    {
        offset = address - 0xA15180;
        return address >= 0xA15180 && address <= 0xA1519F;
    }

    /// <summary>The 32X's own 256-entry palette ($A15200-$A153FF), confirmed against
    /// <c>Pico32xMem.pal[0x100]</c> (<c>pico_int.h:693</c>).</summary>
    private static bool Is32XPaletteRegister(uint address, out uint offset)
    {
        offset = address - 0xA15200;
        return address >= 0xA15200 && address <= 0xA153FF;
    }

    /// <summary>The 32X frame buffer's plain read/write window ($840000-$85FFFF) — reads and
    /// writes go straight to whichever bank the SH-2s currently write to (see
    /// <see cref="Sega32X"/>'s own remarks on display-vs-write bank selection).</summary>
    private static bool Is32XFrameBufferWindow(uint address, out uint offset)
    {
        offset = address - 0x840000;
        return address >= 0x840000 && address <= 0x85FFFF;
    }

    /// <summary>The frame buffer's "overwrite" mirror ($860000-$87FFFF) — the same underlying
    /// bank as <see cref="Is32XFrameBufferWindow"/>, but word writes mask out zero bytes/words so
    /// the existing value shows through underneath (confirmed in Phase 2's own research pass).</summary>
    private static bool Is32XFrameBufferOverwriteWindow(uint address, out uint offset)
    {
        offset = address - 0x860000;
        return address >= 0x860000 && address <= 0x87FFFF;
    }

    /// <summary>The 68000-side ROM banking window ($900000-$9FFFFF) — real 32X hardware maps a
    /// selectable 1MB slice of the SAME cartridge ROM already visible at $000000-$3FFFFF here, bank
    /// number chosen by the adapter register block's byte offset 5 (confirmed against PicoDrive's
    /// <c>bank_switch_rom_68k</c>/its write-dispatch <c>case 0x05: // bank</c>,
    /// <c>reference/PicoDrive/picodrive/pico/32x/memory.c:439-444,1449-1484</c>). Real 32X boot
    /// code reads through here to reach ROM content beyond whatever fixed portion the base
    /// cartridge window exposes — Phase 2 explicitly deferred this (its own synthetic
    /// bus-integration test never needed it), which is exactly what left real, unmodified 32X ROMs
    /// unable to boot: their own init code jumping in here read open bus (an all-<c>$FFFF</c>
    /// illegal opcode) and got stuck. See <see cref="Sega32X.ReadRomBankWindowByte"/> for the
    /// actual bank computation — this predicate is bus-decode only.</summary>
    private static bool Is32XRomBankWindow(uint address, out uint offset)
    {
        offset = address - 0x900000;
        return address >= 0x900000 && address <= 0x9FFFFF;
    }

    /// <summary>The 68000-side unbanked ROM mirror ($880000-$8FFFFF) — always shows cartridge ROM
    /// starting from offset 0 (up to 512KB), completely ignoring the bank-select register that
    /// <see cref="Is32XRomBankWindow"/> honors. A separate, real gap from that banked window: real
    /// 32X boot code uses this specifically because it's a *stable* way to reach a fixed ROM
    /// offset regardless of whatever bank the $900000 window currently has selected. Confirmed
    /// against PicoDrive's own <c>PicoMemSetup32x</c> ("32X ROM (unbanked...)",
    /// <c>reference/PicoDrive/picodrive/pico/32x/memory.c:2367-2372</c>), which maps
    /// <c>Pico.rom</c> directly here with no bank offset at all. Found via the same real-ROM
    /// investigation as the banked window: a real title's boot code jumps through here right
    /// after releasing the SH-2s, and with this unmapped, that jump read open bus and the whole
    /// boot sequence looped back to the start.</summary>
    private static bool Is32XRomMirrorWindow(uint address, out uint offset)
    {
        offset = address - 0x880000;
        return address >= 0x880000 && address <= 0x8FFFFF;
    }

    /// <summary>What every Cpu68000.IBus read method falls back to once none of the specific
    /// regions above (ROM, work RAM, VDP, Z80 window/registers, controllers, TMSS) match: real
    /// hardware's bus arbiter doesn't generate a bus error for addresses no chip is wired to
    /// respond to — reads just float to open bus (0xFF), and (per the write methods, which
    /// simply fall through and do nothing in the same situation) writes go nowhere.
    ///
    /// Found via real-ROM testing, twice: a homebrew rhythm game (Scorpion Illuminati) writes
    /// to 0xA12006 — not a register this emulator or, as far as I know, real hardware assigns
    /// any meaning to — during early boot; and a commercial-scale SGDK game (Omega Blast) has
    /// its Z80 sound driver legitimately bank-switch through its 68000-side window into
    /// 0x815337, well past this cartridge's actual ROM size, while fetching driver code that
    /// otherwise runs correctly for several real frames beforehand. Both used to crash the
    /// emulator outright instead of getting the harmless value real hardware would give them.</summary>
    private const byte OpenBusReadFallback = 0xFF;

    /// <summary>Each register slot is nominally one byte wide (0x00 version, 0x02/0x04/0x06
    /// the three ports' data registers, 0x08/0x0A/0x0C their direction registers, 0x0E-0x1F
    /// unmodeled serial registers) but conventionally accessed a word at a time — the odd
    /// address right after each slot is treated as an alias for the same slot rather than a
    /// distinct register, same leniency as the bus-request/reset registers above.</summary>
    private byte ReadControllerRegisterByte(uint offset) => (offset & ~1u) switch
    {
        0x00 => VersionRegisterValue,
        0x02 => ControllerPort1.ReadData(),
        0x04 => ControllerPort2.ReadData(),
        0x06 => ExtPort.ReadData(),
        0x08 => ControllerPort1.Direction,
        0x0A => ControllerPort2.Direction,
        0x0C => ExtPort.Direction,
        _ => 0xFF,
    };

    private void WriteControllerRegisterByte(uint offset, byte value)
    {
        switch (offset & ~1u)
        {
            case 0x02: ControllerPort1.WriteData(value, Cpu.TotalCycles); break;
            case 0x04: ControllerPort2.WriteData(value, Cpu.TotalCycles); break;
            case 0x06: ExtPort.WriteData(value, Cpu.TotalCycles); break;
            case 0x08: ControllerPort1.Direction = value; break;
            case 0x0A: ControllerPort2.Direction = value; break;
            case 0x0C: ExtPort.Direction = value; break;
            // 0x00 (version) is read-only; the serial registers (0x0E-0x1F) are no-ops.
        }
    }

    /// <summary>Routes a byte through to the Z80's own address decode, guarded against the
    /// bank-window recursion described on <see cref="_crossBusAccessInProgress"/>. Returns
    /// open bus (0xFF) if the bus hasn't been requested or the guard is already tripped.</summary>
    private byte ReadZ80WindowByte(uint address)
    {
        if (!_z80BusRequested || _crossBusAccessInProgress)
        {
            return 0xFF;
        }

        _crossBusAccessInProgress = true;
        try
        {
            return ((CpuZ80.IBus)this).ReadByte((ushort)address);
        }
        finally
        {
            _crossBusAccessInProgress = false;
        }
    }

    private void WriteZ80WindowByte(uint address, byte value)
    {
        if (!_z80BusRequested || _crossBusAccessInProgress)
        {
            return;
        }

        _crossBusAccessInProgress = true;
        try
        {
            ((CpuZ80.IBus)this).WriteByte((ushort)address, value);
        }
        finally
        {
            _crossBusAccessInProgress = false;
        }
    }

    // Cpu68000.IBus — 68000 address space. ROM, work RAM, VDP ports, the Z80 bus-request/reset
    // registers, and (once the bus is requested) the Z80's own address space are wired up.
    // Sub-addresses of the 0xA10000-0xA1FFFF I/O area not assigned to a specific register are
    // open bus (see IsUnmappedIoArea), not an error.
    byte Cpu68000.IBus.ReadByte(uint address)
    {
        // Real 68000 hardware only has 24 address pins (A1-A23) -- any garbage in an address
        // register's upper 8 bits (extremely common; software routinely leaves them untouched,
        // relying on the bus itself to ignore them) is truncated before the value ever reaches
        // memory decoding. Without this mask, a value like 0x503BCF6 would fail the ROM check
        // (< 0x400000) and the wide work-RAM mirror check (>= 0xE00000) would wrongly claim it,
        // silently substituting unrelated RAM contents for real ROM data.
        address &= 0xFFFFFF;

        if (IsVdpPort(address, out uint portOffset))
        {
            // See ReadVdpPortWord's remarks -- every offset within a port's own span now
            // dispatches to that same register, so no masking is needed here. A byte read at an
            // odd port address (e.g. $C00005, the control port's low byte -- exactly what a BTST
            // on a single status bit uses) used to fall through to that switch's default case
            // and always read back 0, regardless of the real status bit's value -- confirmed via
            // a real ROM (Altered Beast) that hangs forever polling bit 3 (VBlank) at that exact
            // address for precisely this reason.
            ushort word = ReadVdpPortWord(portOffset);
            return (address & 1) == 0 ? (byte)(word >> 8) : (byte)word;
        }

        if (IsZ80BusRequestRegister(address))
        {
            if ((address & 1) != 0)
            {
                return OpenBusReadFallback;
            }

            // Real hardware only actually drives bit 0 here -- the other 7 bits reflect
            // whatever the 68000 currently has prefetched at its own PC, an undocumented
            // open-bus quirk (confirmed against genesis-plus-gx's ctrl_io_read_byte: "Unused
            // bits return prefetched bus data (Time Killers)"). Some games (Shadow of the
            // Beast) poll this with TST.B rather than BTST #0 -- returning a clean 0x00/0x01
            // here made their busreq-acknowledge wait loop spin forever (TST.B kept seeing an
            // all-zero byte) instead of exiting almost immediately like real hardware, where
            // the prefetch noise in the upper bits almost never happens to also be zero.
            byte prefetched = ((Cpu68000.IBus)this).ReadByte(Cpu.PC);
            return _z80BusRequested ? (byte)(prefetched & 0xFE) : (byte)(prefetched | 0x01);
        }

        if (IsZ80ResetRegister(address))
        {
            return (byte)(_z80Held ? 0x00 : 0x01); // 1 = running normally, 0 = held in reset
        }

        if (IsZ80Window(address))
        {
            return ReadZ80WindowByte(address);
        }

        if (IsControllerRegister(address, out uint controllerOffset))
        {
            return ReadControllerRegisterByte(controllerOffset);
        }

        if (IsTmssRegister(address, out uint tmssOffset))
        {
            return _tmssRegister[tmssOffset];
        }

        if (Is32XRegister(address, out uint reg32XOffset))
        {
            return Sega32X.ReadControlByteFor68k(reg32XOffset);
        }

        if (IsMarsIdRegister(address, out uint marsOffset))
        {
            return MarsId[marsOffset];
        }

        if (Is32XVdpRegister(address, out uint vdp32XOffset))
        {
            return Sega32X.ReadVdpControlByteFor68k(vdp32XOffset);
        }

        if (Is32XPaletteRegister(address, out uint pal32XOffset))
        {
            return Sega32X.ReadPaletteByteFor68k(pal32XOffset);
        }

        if (Is32XFrameBufferWindow(address, out uint fbOffset) || Is32XFrameBufferOverwriteWindow(address, out fbOffset))
        {
            return Sega32X.ReadFrameBufferByteFor68k(fbOffset);
        }

        if (Is32XRomBankWindow(address, out uint bankOffset))
        {
            return Sega32X.ReadRomBankWindowByte(bankOffset);
        }

        if (Is32XRomMirrorWindow(address, out uint mirrorOffset))
        {
            return Sega32X.ReadRomMirrorWindowByte(mirrorOffset);
        }

        if (address < 0x400000 && Cartridge.Rom.Length > 0)
        {
            return Cartridge.Rom[address % (uint)Cartridge.Rom.Length];
        }

        // Nominally $FF0000-$FFFFFF, but real hardware's RAM chip-select only decodes the top
        // 3 address bits (A21-A23 = 111), ignoring the rest -- so work RAM is actually mirrored
        // across the entire $E00000-$FFFFFF range, and Sega's own code (Sonic 1/2, confirmed by
        // real-ROM testing) relies on this, addressing RAM through mirrors like $E0FF0974 as
        // well as the nominal $FF0974. Restricting this to >=0xFF0000 alone made those reads
        // fall through to open bus, corrupting a pointer and eventually running code off into
        // unrelated bytes.
        if (address >= 0xE00000)
        {
            return _workRam[address & 0xFFFF];
        }

        return OpenBusReadFallback;
    }

    ushort Cpu68000.IBus.ReadWord(uint address)
    {
        // See ReadByte's remarks -- real hardware's 24-bit address bus truncates anything above it.
        address &= 0xFFFFFF;

        if (IsVdpPort(address, out uint portOffset))
        {
            return ReadVdpPortWord(portOffset);
        }

        if (IsZ80BusRequestRegister(address))
        {
            // Same prefetch-noise quirk as the byte-read path -- only bit 8 (the high byte's
            // LSB) is actually driven; the rest of the word reflects whatever the 68000
            // currently has prefetched at its own PC.
            ushort prefetched = ((Cpu68000.IBus)this).ReadWord(Cpu.PC);
            return _z80BusRequested ? (ushort)(prefetched & 0xFEFF) : (ushort)(prefetched | 0x0100);
        }

        if (IsZ80ResetRegister(address))
        {
            return (ushort)(_z80Held ? 0x0000 : 0x0100);
        }

        if (IsZ80Window(address))
        {
            return (ushort)((ReadZ80WindowByte(address) << 8) | ReadZ80WindowByte(address + 1));
        }

        if (IsControllerRegister(address, out uint controllerOffset))
        {
            byte b = ReadControllerRegisterByte(controllerOffset);
            return (ushort)((b << 8) | b);
        }

        if (IsTmssRegister(address, out uint tmssOffset))
        {
            return (ushort)((_tmssRegister[tmssOffset] << 8) | _tmssRegister[(tmssOffset + 1) & 3]);
        }

        if (Is32XRegister(address, out uint reg32XOffset))
        {
            return (ushort)((Sega32X.ReadControlByteFor68k(reg32XOffset) << 8) | Sega32X.ReadControlByteFor68k(reg32XOffset + 1));
        }

        if (IsMarsIdRegister(address, out uint marsOffset))
        {
            return (ushort)((MarsId[marsOffset] << 8) | MarsId[(marsOffset + 1) % 4]);
        }

        if (Is32XVdpRegister(address, out uint vdp32XOffset))
        {
            return (ushort)((Sega32X.ReadVdpControlByteFor68k(vdp32XOffset) << 8) | Sega32X.ReadVdpControlByteFor68k(vdp32XOffset + 1));
        }

        if (Is32XPaletteRegister(address, out uint pal32XOffset))
        {
            return (ushort)((Sega32X.ReadPaletteByteFor68k(pal32XOffset) << 8) | Sega32X.ReadPaletteByteFor68k(pal32XOffset + 1));
        }

        if (Is32XFrameBufferWindow(address, out uint fbOffset) || Is32XFrameBufferOverwriteWindow(address, out fbOffset))
        {
            return Sega32X.ReadFrameBufferWordFor68k(fbOffset);
        }

        if (Is32XRomBankWindow(address, out uint bankOffset))
        {
            return (ushort)((Sega32X.ReadRomBankWindowByte(bankOffset) << 8) | Sega32X.ReadRomBankWindowByte(bankOffset + 1));
        }

        if (Is32XRomMirrorWindow(address, out uint mirrorOffset))
        {
            return (ushort)((Sega32X.ReadRomMirrorWindowByte(mirrorOffset) << 8) | Sega32X.ReadRomMirrorWindowByte(mirrorOffset + 1));
        }

        if (address < 0x400000 && Cartridge.Rom.Length > 0)
        {
            uint index = address % (uint)Cartridge.Rom.Length;
            return (ushort)((Cartridge.Rom[index] << 8) | Cartridge.Rom[(index + 1) % (uint)Cartridge.Rom.Length]);
        }

        // See ReadByte's remarks -- work RAM mirrors across all of $E00000-$FFFFFF.
        if (address >= 0xE00000)
        {
            uint offset = address & 0xFFFF;
            return (ushort)((_workRam[offset] << 8) | _workRam[(offset + 1) & 0xFFFF]);
        }

        return (ushort)((OpenBusReadFallback << 8) | OpenBusReadFallback);
    }

    uint Cpu68000.IBus.ReadLong(uint address)
    {
        var bus = (Cpu68000.IBus)this;
        return ((uint)bus.ReadWord(address) << 16) | bus.ReadWord(address + 2);
    }

    void Cpu68000.IBus.WriteByte(uint address, byte value)
    {
        // See ReadByte's remarks -- real hardware's 24-bit address bus truncates anything above it.
        address &= 0xFFFFFF;

        if (IsVdpPort(address, out uint portOffset))
        {
            WriteVdpPortWord(portOffset, (ushort)((value << 8) | value));
            return;
        }

        if (IsZ80BusRequestRegister(address))
        {
            _z80BusRequested = (value & 0x01) != 0;
            return;
        }

        if (IsZ80ResetRegister(address))
        {
            SetZ80Held(!((value & 0x01) != 0));
            return;
        }

        if (IsZ80Window(address))
        {
            WriteZ80WindowByte(address, value);
            return;
        }

        if (IsControllerRegister(address, out uint controllerOffset))
        {
            WriteControllerRegisterByte(controllerOffset, value);
            return;
        }

        if (IsTmssRegister(address, out uint tmssOffset))
        {
            _tmssRegister[tmssOffset] = value;
            return;
        }

        if (Is32XRegister(address, out uint reg32XOffset))
        {
            Sega32X.WriteControlByteFrom68k(reg32XOffset, value);
            return;
        }

        if (IsMarsIdRegister(address, out _))
        {
            return; // read-only hardware-presence probe
        }

        if (Is32XVdpRegister(address, out uint vdp32XOffset))
        {
            Sega32X.WriteVdpControlByteFrom68k(vdp32XOffset, value);
            return;
        }

        if (Is32XPaletteRegister(address, out uint pal32XOffset))
        {
            Sega32X.WritePaletteByteFrom68k(pal32XOffset, value);
            return;
        }

        if (Is32XFrameBufferWindow(address, out uint fbOffset))
        {
            Sega32X.WriteFrameBufferByteFrom68k(fbOffset, value);
            return;
        }

        if (Is32XFrameBufferOverwriteWindow(address, out fbOffset))
        {
            Sega32X.WriteFrameBufferByteFrom68k(fbOffset, value);
            return;
        }

        // Read-only, matching plain cartridge ROM's own write-drop convention (see ReadByte's
        // remarks) -- real 32X boot code never writes through this window, only reads.
        if (Is32XRomBankWindow(address, out _))
        {
            return;
        }

        if (Is32XRomMirrorWindow(address, out _))
        {
            return;
        }

        // See ReadByte's remarks -- work RAM mirrors across all of $E00000-$FFFFFF.
        if (address >= 0xE00000)
        {
            _workRam[address & 0xFFFF] = value;
            return;
        }

        // Cartridge ROM has no write line wired on real hardware -- a write here (most
        // commonly an interrupt handler pushing PC/SR at an uninitialized or garbage stack
        // pointer, which real homebrew testing turned up almost immediately) is silently
        // dropped, not an error.
        if (address < 0x400000)
        {
            return;
        }
    }

    void Cpu68000.IBus.WriteWord(uint address, ushort value)
    {
        // See ReadByte's remarks -- real hardware's 24-bit address bus truncates anything above it.
        address &= 0xFFFFFF;

        if (IsVdpPort(address, out uint portOffset))
        {
            WriteVdpPortWord(portOffset, value);
            return;
        }

        if (IsZ80BusRequestRegister(address))
        {
            _z80BusRequested = (value & 0x0100) != 0;
            return;
        }

        if (IsZ80ResetRegister(address))
        {
            SetZ80Held(!((value & 0x0100) != 0));
            return;
        }

        if (IsZ80Window(address))
        {
            WriteZ80WindowByte(address, (byte)(value >> 8));
            WriteZ80WindowByte(address + 1, (byte)value);
            return;
        }

        if (IsControllerRegister(address, out uint controllerOffset))
        {
            WriteControllerRegisterByte(controllerOffset, (byte)value);
            return;
        }

        if (IsTmssRegister(address, out uint tmssOffset))
        {
            _tmssRegister[tmssOffset] = (byte)(value >> 8);
            _tmssRegister[(tmssOffset + 1) & 3] = (byte)value;
            return;
        }

        if (Is32XRegister(address, out uint reg32XOffset))
        {
            Sega32X.WriteControlByteFrom68k(reg32XOffset, (byte)(value >> 8));
            Sega32X.WriteControlByteFrom68k(reg32XOffset + 1, (byte)value);
            return;
        }

        if (IsMarsIdRegister(address, out _))
        {
            return; // read-only hardware-presence probe
        }

        if (Is32XVdpRegister(address, out uint vdp32XOffset))
        {
            Sega32X.WriteVdpControlByteFrom68k(vdp32XOffset, (byte)(value >> 8));
            Sega32X.WriteVdpControlByteFrom68k(vdp32XOffset + 1, (byte)value);
            return;
        }

        if (Is32XPaletteRegister(address, out uint pal32XOffset))
        {
            Sega32X.WritePaletteByteFrom68k(pal32XOffset, (byte)(value >> 8));
            Sega32X.WritePaletteByteFrom68k(pal32XOffset + 1, (byte)value);
            return;
        }

        if (Is32XFrameBufferWindow(address, out uint fbOffset))
        {
            Sega32X.WriteFrameBufferWordFrom68k(fbOffset, overwrite: false, value);
            return;
        }

        if (Is32XFrameBufferOverwriteWindow(address, out fbOffset))
        {
            Sega32X.WriteFrameBufferWordFrom68k(fbOffset, overwrite: true, value);
            return;
        }

        if (Is32XRomBankWindow(address, out _))
        {
            return;
        }

        if (Is32XRomMirrorWindow(address, out _))
        {
            return;
        }

        // See ReadByte's remarks -- work RAM mirrors across all of $E00000-$FFFFFF.
        if (address >= 0xE00000)
        {
            uint offset = address & 0xFFFF;
            _workRam[offset] = (byte)(value >> 8);
            _workRam[(offset + 1) & 0xFFFF] = (byte)value;
            return;
        }

        // See the identical comment in WriteByte -- ROM is read-only on real hardware.
        if (address < 0x400000)
        {
            return;
        }
    }

    /// <summary>The Z80's registers are cleared at the moment reset is asserted (the 0->1
    /// edge of "held"), not at release — since nothing executes while held anyway, the
    /// observable result is identical to clearing at release, but this way there's only one
    /// code path.</summary>
    private void SetZ80Held(bool held)
    {
        if (held && !_z80Held)
        {
            SoundCpu.Reset();
        }

        _z80Held = held;
    }

    void Cpu68000.IBus.WriteLong(uint address, uint value)
    {
        var bus = (Cpu68000.IBus)this;
        bus.WriteWord(address, (ushort)(value >> 16));
        bus.WriteWord(address + 2, (ushort)value);
    }

    /// <summary>Offsets 0-3 are the data port, 4-7 the control port, 8-0xF the HV counter --
    /// each is a genuine 16-bit register that real hardware mirrors across its own whole 4-byte
    /// (or 8-byte, for the HV counter) span, so EVERY offset in that span -- not just the two
    /// word-aligned ones -- must dispatch to the same register. A byte access at any of those
    /// offsets is an alias for the other byte of the same word (see <see
    /// cref="Cpu68000.IBus.ReadByte"/>'s remarks), not a distinct register. Real hardware mirrors
    /// this whole 32-byte block across 0xC00000-0xDFFFFF; only the base block is decoded here.
    ///
    /// Only the word-register spans get every offset -- 0x11 (the PSG, below) stays its own
    /// single exact case: real hardware wires the PSG to just that one odd address, not a
    /// word-aliased register, so widening ITS case the same way would wrongly also match 0x10
    /// (unused/open bus) and, worse, wrongly stop matching 0x11 itself if it were rounded down
    /// to 0x10 instead -- confirmed via a real regression when an earlier version of this fix
    /// rounded every odd offset down uniformly rather than widening each port's own real span.</summary>
    private ushort ReadVdpPortWord(uint portOffset) => portOffset switch
    {
        0 or 1 or 2 or 3 => Vdp.ReadDataPort(),
        4 or 5 or 6 or 7 => Vdp.ReadControlPort(),
        >= 8 and <= 0xF => Vdp.ReadHvCounter(),
        _ => 0,
    };

    private void WriteVdpPortWord(uint portOffset, ushort value)
    {
        switch (portOffset)
        {
            case 0 or 1 or 2 or 3: Vdp.WriteDataPort(value); break;
            case 4 or 5 or 6 or 7: Vdp.WriteControlPort(value); break;
            // The PSG is wired to both buses on real hardware: the 68000 can write it directly
            // here (0xC00011) without going through the Z80 at all, same chip as 0x7F11 on the
            // Z80's own bus below.
            case 0x11: Psg.Write((byte)value); break;
        }
    }

    // CpuZ80.IBus — the Z80's full memory map: its own 8KB work RAM (mirrored across
    // 0x0000-0x3FFF), the YM2612 (0x4000-0x5FFF, mirrored — real hardware only decodes the low
    // 2 bits across that block), the bank register (0x6000-0x6FFF), the PSG (0x7F00-0x7FFF),
    // and its banked 32KB window into the 68000's address space (0x8000-0xFFFF). Nothing on
    // this side uses real Z80 I/O ports (IN/OUT) — everything is memory-mapped, so ReadPort/
    // WritePort are never legitimately reached.
    byte CpuZ80.IBus.ReadByte(ushort address)
    {
        if (address < 0x4000)
        {
            return _z80Ram[address & 0x1FFF];
        }

        if (address < 0x6000)
        {
            // The YM2612's registers are write-only; any of the four mirrored addresses reads
            // back the same status byte (busy/timer-overflow flags).
            return Ym2612.ReadStatus();
        }

        if (address < 0x7F00)
        {
            return 0xFF; // bank register block + unused: open bus, nothing to read
        }

        if (address < 0x8000)
        {
            return 0xFF; // PSG is write-only
        }

        return ((Cpu68000.IBus)this).ReadByte(Z80BankWindowBase + (uint)(address - 0x8000));
    }

    void CpuZ80.IBus.WriteByte(ushort address, byte value)
    {
        if (address < 0x4000)
        {
            _z80Ram[address & 0x1FFF] = value;
            return;
        }

        if (address < 0x6000)
        {
            switch (address & 0x03)
            {
                case 0: Ym2612.WriteAddressPart1(value); break;
                case 1: Ym2612.WriteDataPart1(value); break;
                case 2: Ym2612.WriteAddressPart2(value); break;
                case 3: Ym2612.WriteDataPart2(value); break;
            }

            return;
        }

        if (address < 0x7000)
        {
            // 9-bit bank register, shifted in one bit at a time (LSB first) — see
            // _z80BankRegister's doc comment.
            _z80BankRegister = (ushort)((_z80BankRegister >> 1) | ((value & 0x01) << 8));
            return;
        }

        if (address < 0x7F00)
        {
            return; // unused
        }

        if (address < 0x8000)
        {
            Psg.Write(value);
            return;
        }

        ((Cpu68000.IBus)this).WriteByte(Z80BankWindowBase + (uint)(address - 0x8000), value);
    }

    private uint Z80BankWindowBase => (uint)_z80BankRegister << 15;

    /// <summary>Real Z80 IN/OUT ports aren't wired to anything on the Genesis — every chip the
    /// Z80 talks to (YM2612, PSG, the bank register, the 68000 bus window) is memory-mapped
    /// instead. Found via real-ROM testing that a genuine IN/OUT can still execute (whether
    /// from legitimate driver code this core doesn't otherwise account for, or from the Z80
    /// having wandered into non-code memory and decoded a stray IN/OUT byte — same class of
    /// issue as the M68000 side's open-bus fixes). Real hardware doesn't fault on either: reads
    /// float to open bus, writes go nowhere.</summary>
    byte CpuZ80.IBus.ReadPort(byte port) => 0xFF;
    void CpuZ80.IBus.WritePort(byte port, byte value) { }
}
