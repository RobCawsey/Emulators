namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Checks the supervisor bit before a privileged instruction touches anything.
    /// If it's not set, raises the privilege-violation exception (vector 8) and tells the
    /// caller to bail out immediately — no operand fetches, no state changes.</summary>
    private bool RequireSupervisor(out int trapCycles)
    {
        if (Supervisor)
        {
            trapCycles = 0;
            return true;
        }

        RaiseException(8);
        trapCycles = 34;
        return false;
    }

    /// <summary>STOP — privileged. Loads SR from the immediate word that follows and halts
    /// fetch/execute (see <see cref="Stopped"/>).</summary>
    private int ExecuteStop(ushort opcode)
    {
        if (!RequireSupervisor(out int trapCycles))
        {
            return trapCycles;
        }

        SR = FetchWord();
        Stopped = true;
        return 4;
    }

    /// <summary>RESET — privileged. Pulses the external reset line for peripherals; the
    /// 68000's own registers are untouched.</summary>
    private int ExecuteReset(ushort opcode)
    {
        if (!RequireSupervisor(out int trapCycles))
        {
            return trapCycles;
        }

        ExternalDevicesReset?.Invoke();
        return 132;
    }

    /// <summary>TRAPV — not privileged. Traps via vector 7 if V is set; otherwise a no-op.</summary>
    private int ExecuteTrapv(ushort opcode)
    {
        if (!FlagOverflow)
        {
            return 4;
        }

        RaiseException(7);
        return 34;
    }

    /// <summary>MOVE to CCR — not privileged on the original MC68000 (this differs from
    /// "MOVE to SR", which is). Only the low byte of the word source is used.</summary>
    private int ExecuteMoveToCcr(ushort opcode)
    {
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);
        uint value = ReadEa(ea, Size.Word);
        SR = (ushort)(((uint)SR & 0xFF00) | (value & 0x00FF));
        return 12 + cycles;
    }

    /// <summary>MOVE from SR — not privileged on the original MC68000 (68010+ made this
    /// privileged; the Genesis's CPU predates that change).</summary>
    private int ExecuteMoveFromSr(ushort opcode)
    {
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);
        WriteEa(ea, Size.Word, SR);
        return 6 + cycles;
    }

    /// <summary>MOVE to SR — privileged. Replaces the whole status register, including the
    /// supervisor bit and interrupt mask.</summary>
    private int ExecuteMoveToSr(ushort opcode)
    {
        if (!RequireSupervisor(out int trapCycles))
        {
            return trapCycles;
        }

        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);
        SR = (ushort)ReadEa(ea, Size.Word);
        return 12 + cycles;
    }

    /// <summary>MOVE An,USP / MOVE USP,An — privileged. The Genesis's 68000 always boots (and
    /// virtually every real game stays) in supervisor mode, so this doesn't hook into an
    /// automatic SSP/USP swap on S-bit changes — nothing on this platform actually exercises
    /// that. It's just a second, otherwise-inert 32-bit register real startup code sometimes
    /// initializes and never revisits, and this stores/retrieves exactly that.</summary>
    private int ExecuteMoveUsp(ushort opcode)
    {
        if (!RequireSupervisor(out int trapCycles))
        {
            return trapCycles;
        }

        int register = opcode & 7;
        bool toUsp = (opcode & 0x08) == 0;

        if (toUsp)
        {
            _usp = A[register];
        }
        else
        {
            A[register] = _usp;
        }

        return 4;
    }

    /// <summary>ANDI/ORI/EORI to CCR (byte, unprivileged) or to SR (word, privileged) — the
    /// "immediate mode as EA" special form that <see cref="ExecuteImmediateLogic"/> defers to.</summary>
    private int ExecuteImmediateLogicToStatusRegister(Size size, Func<uint, uint, uint> combine)
    {
        if (size == Size.Word)
        {
            if (!RequireSupervisor(out int trapCycles))
            {
                return trapCycles;
            }

            uint immediate = ReadEa(EffectiveAddress.Memory(FetchImmediate(Size.Word)), Size.Word);
            SR = (ushort)combine(SR, immediate);
            return 20;
        }

        uint immediateByte = ReadEa(EffectiveAddress.Memory(FetchImmediate(Size.Byte)), Size.Byte);
        SR = (ushort)(((uint)SR & 0xFF00) | (combine((uint)SR & 0xFF, immediateByte) & 0xFF));
        return 20;
    }
}
