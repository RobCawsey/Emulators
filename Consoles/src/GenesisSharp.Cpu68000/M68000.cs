namespace GenesisSharp.Cpu68000;

/// <summary>Motorola 68000 core — main CPU of the Genesis/Mega Drive.</summary>
public sealed partial class M68000
{
    private readonly IBus _bus;

    public uint[] D { get; } = new uint[8];
    public uint[] A { get; } = new uint[8];
    public uint PC { get; set; }
    public ushort SR { get; set; }
    public long TotalCycles { get; private set; }

    /// <summary>Fires with the PC of the next opcode about to be fetched, right before that
    /// fetch — debugging hook only (tracing/breakpoint tools), not part of emulation itself.
    /// Skipped when an interrupt is serviced or the CPU is <see cref="Stopped"/> that
    /// instruction slot, matching the doc comment on <see cref="Step"/>.</summary>
    public event Action<uint>? InstructionFetching;

    /// <summary>See <see cref="ExecuteMoveUsp"/> — a second stack-pointer-shaped register,
    /// not automatically swapped with A[7] on supervisor/user mode changes.</summary>
    private uint _usp;

    /// <summary>Set by the STOP instruction, and cleared automatically once a qualifying
    /// interrupt is serviced (see <see cref="RaiseInterrupt"/>) — matching real hardware,
    /// which resumes STOP on an unmasked interrupt, trace, or external reset. Trace isn't
    /// modeled, and external reset is a hardware-level concept this emulator doesn't need a
    /// hook for (GenesisSharp.Core would just call <see cref="Reset"/> directly).</summary>
    public bool Stopped { get; private set; }

    /// <summary>0 = no interrupt request pending. 1-7 mirrors the 68000's IPL2-0 lines: the
    /// current request level, checked against SR's interrupt priority mask at each
    /// instruction boundary. See <see cref="RaiseInterrupt"/>.</summary>
    public int PendingInterruptLevel { get; private set; }

    /// <summary>Raised by the RESET instruction, which pulses the external reset line for
    /// peripherals without resetting the 68000 itself. The CPU core has no reference to the
    /// VDP/PSG/YM2612, so it just notifies whoever wired this up (GenesisSharp.Core).</summary>
    public event Action? ExternalDevicesReset;

    public M68000(IBus bus)
    {
        _bus = bus;
    }

    public void Reset()
    {
        Array.Clear(D);
        Array.Clear(A);
        SR = 0x2700;
        A[7] = _bus.ReadLong(0x0000_0000);
        PC = _bus.ReadLong(0x0000_0004);
        TotalCycles = 0;
        Stopped = false;
        PendingInterruptLevel = 0;
        _usp = 0;
    }

    /// <summary>Clears the STOP state without servicing an interrupt — not how real hardware
    /// resumes, but kept for callers that just want to force the core running again (e.g.
    /// tests, or a debugger-style "unstick" action).</summary>
    public void Resume() => Stopped = false;

    /// <summary>Executes one instruction and returns the number of clock cycles it took.
    /// Interrupt delivery is checked first, exactly like real hardware checks IPL between
    /// every instruction (including while stopped) — see <see cref="RaiseInterrupt"/>. While
    /// <see cref="Stopped"/> and no qualifying interrupt is pending, this just burns idle
    /// cycles instead of fetching.</summary>
    public int Step()
    {
        int interruptCycles = ServicePendingInterrupt();
        if (interruptCycles > 0)
        {
            Stopped = false;
            TotalCycles += interruptCycles;
            return interruptCycles;
        }

        if (Stopped)
        {
            TotalCycles += 4;
            return 4;
        }

        InstructionFetching?.Invoke(PC);
        ushort opcode = FetchWord();
        int cycles;
        try
        {
            cycles = Decode(opcode);
        }
        catch (ReservedOpcodeException)
        {
            // A decode helper (e.g. DecodeImmSize) hit an architecturally-reserved bit
            // pattern partway through decoding, before any register/memory state changed —
            // see ReservedOpcodeException's doc comment. Real hardware traps here instead of
            // executing anything; PC is already correctly positioned past the opcode word.
            cycles = RaiseIllegalInstruction();
        }

        TotalCycles += cycles;
        return cycles;
    }

    private ushort FetchWord()
    {
        ushort value = _bus.ReadWord(PC);
        PC += 2;
        return value;
    }

    private uint FetchLong()
    {
        uint value = _bus.ReadLong(PC);
        PC += 4;
        return value;
    }

    private uint ReadRegister(bool addressRegister, int index) => addressRegister ? A[index] : D[index];

    private void WriteDataRegister(int index, Size size, uint value)
    {
        D[index] = size == Size.Long ? value : (D[index] & ~size.Mask()) | (value & size.Mask());
    }

    /// <summary>Address registers always hold a full 32-bit value; a word-sized write is
    /// sign-extended, matching real hardware (there is no instruction that can write only the
    /// low 16 bits of An and leave the top half alone).</summary>
    private void WriteAddressRegister(int index, Size size, uint value) => A[index] = size.SignExtend(value);

    private void PushLong(uint value)
    {
        A[7] -= 4;
        _bus.WriteLong(A[7], value);
    }

    private uint PopLong()
    {
        uint value = _bus.ReadLong(A[7]);
        A[7] += 4;
        return value;
    }

    private ushort PopWord()
    {
        ushort value = _bus.ReadWord(A[7]);
        A[7] += 2;
        return value;
    }

    private void PushWord(ushort value)
    {
        A[7] -= 2;
        _bus.WriteWord(A[7], value);
    }
}
