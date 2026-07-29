namespace GenesisSharp.CpuZ80;

/// <summary>Zilog Z80 core — drives the Genesis's sound driver (and, in SMS-compatibility
/// mode, the whole system).</summary>
public sealed partial class Z80
{
    private readonly IBus _bus;

    public byte A, F, B, C, D, E, H, L;
    public byte AltA, AltF, AltB, AltC, AltD, AltE, AltH, AltL;
    public ushort IX { get; set; }
    public ushort IY { get; set; }
    public ushort SP { get; set; }
    public ushort PC { get; set; }

    /// <summary>Interrupt vector register (high byte of the interrupt vector table address in
    /// IM 2) and memory refresh register — real hardware increments R every M1 (opcode fetch)
    /// cycle; this emulator increments it once per instruction, which is enough for programs
    /// that read it via LD A,R but not cycle-exact.</summary>
    public byte I, R;

    public bool Iff1, Iff2;
    public int InterruptMode;
    public bool Halted { get; private set; }
    public long TotalCycles { get; private set; }

    public Z80(IBus bus)
    {
        _bus = bus;
    }

    public void Reset()
    {
        A = F = B = C = D = E = H = L = 0;
        AltA = AltF = AltB = AltC = AltD = AltE = AltH = AltL = 0;
        IX = IY = 0;
        I = R = 0;
        Iff1 = Iff2 = false;
        InterruptMode = 0;
        PC = 0;
        SP = 0xFFFF;
        Halted = false;
        TotalCycles = 0;
        NmiPending = false;
        InterruptPending = false;
    }

    /// <summary>Executes one instruction and returns the number of clock cycles it took.
    /// Interrupt delivery is checked first, exactly like real hardware samples NMI/INT between
    /// every instruction (including while halted) — see <see cref="RaiseNonMaskableInterrupt"/>
    /// and <see cref="RaiseMaskableInterrupt"/>. HALT is otherwise modeled by re-executing NOPs
    /// at 4 cycles each, matching real hardware, which keeps fetching and discarding NOPs from
    /// the bus while halted.</summary>
    public int Step()
    {
        int interruptCycles = ServicePendingInterrupt();
        if (interruptCycles > 0)
        {
            TotalCycles += interruptCycles;
            return interruptCycles;
        }

        if (Halted)
        {
            TotalCycles += 4;
            return 4;
        }

        byte opcode = FetchByte();
        int cycles = opcode switch
        {
            // The CB/ED/DD/FD handlers all return the standard *total* published T-state
            // count, which already accounts for the prefix byte's own fetch — don't add
            // another 4 here.
            0xCB => ExecuteCbOpcode(FetchByte()),
            0xED => ExecuteEdOpcode(FetchByte()),
            0xDD => ExecuteIndexedPrefix(IndexMode.Ix),
            0xFD => ExecuteIndexedPrefix(IndexMode.Iy),
            _ => ExecuteMainOpcode(opcode),
        };

        TotalCycles += cycles;
        return cycles;
    }

    private static NotImplementedException Unimplemented(byte opcode, string what) =>
        new($"Opcode 0x{opcode:X2} ({what}) is not implemented yet.");

    private byte FetchByte()
    {
        byte value = _bus.ReadByte(PC);
        PC++;
        R++;
        return value;
    }

    private ushort FetchWord()
    {
        byte low = FetchByte();
        byte high = FetchByte();
        return (ushort)((high << 8) | low);
    }

    private void PushWord(ushort value)
    {
        SP--;
        _bus.WriteByte(SP, (byte)(value >> 8));
        SP--;
        _bus.WriteByte(SP, (byte)value);
    }

    private ushort PopWord()
    {
        byte low = _bus.ReadByte(SP);
        SP++;
        byte high = _bus.ReadByte(SP);
        SP++;
        return (ushort)((high << 8) | low);
    }
}
