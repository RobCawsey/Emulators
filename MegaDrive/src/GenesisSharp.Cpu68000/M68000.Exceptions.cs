namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Signals a genuinely architecturally-reserved opcode bit pattern — as opposed
    /// to a real, valid 68000 instruction this emulator simply hasn't implemented yet (see
    /// <see cref="Unimplemented"/> for that separate, deliberately-distinct case). Thrown from
    /// decode helpers that can't raise the trap themselves because their signature returns a
    /// decoded value, not a cycle count (<see cref="DecodeImmSize"/>'s reserved-size-field
    /// case, for instance); <see cref="Step"/> catches it and converts it into the real
    /// illegal-instruction exception (vector 4) real hardware would raise for the same bit
    /// pattern, rather than crashing the emulator over something a real console would have
    /// simply trapped and (depending on what the ROM's own vector 4 handler does) recovered
    /// from.</summary>
    private sealed class ReservedOpcodeException : Exception;

    /// <summary>Fires whenever <see cref="RaiseException"/> runs, before the vector's handler
    /// starts executing — the vector number and the return-address PC that got pushed. Exists
    /// purely as a debugging hook (tracing tools, headless test harnesses) for spotting a
    /// software-detected trap (illegal instruction, CHK, TRAPV, privilege violation, ...)
    /// firing somewhere real hardware wouldn't; it has no effect on emulation itself.</summary>
    public event Action<int, uint>? ExceptionRaised;

    /// <summary>The standard 68000 exception-entry sequence: push PC (long) then SR (word) —
    /// so SR ends up on top of the stack, which is what RTE expects to pop first — enter
    /// supervisor mode, and load the new PC from the vector table. Used by CHK today; TRAP,
    /// address error, and the interrupt controller will reuse this once they exist.</summary>
    private void RaiseException(int vectorNumber)
    {
        ExceptionRaised?.Invoke(vectorNumber, PC);
        ushort savedSr = SR;
        PushLong(PC);
        PushWord(savedSr);
        SR = (ushort)(SR | SupervisorBit);
        PC = _bus.ReadLong((uint)(vectorNumber * 4));
    }

    /// <summary>Illegal instruction — vector 4, 34 cycles (the same cost as the other
    /// software-detected traps: TRAP, TRAPV, privilege violation). PC at this point already
    /// points past the illegal opcode word, matching where real hardware pushes it, since
    /// <see cref="Step"/>'s <see cref="FetchWord"/> call already advanced past it before
    /// decoding even began.</summary>
    private int RaiseIllegalInstruction()
    {
        RaiseException(4);
        return 34;
    }

    /// <summary>Line A ("Line 1010 Emulator") — vector 10, 34 cycles. Real hardware traps here
    /// unconditionally for the *entire* 0xA000-0xAFFF opcode range: those bits were never
    /// assigned to a real instruction at all, reserved instead for software-defined "line 1010"
    /// emulator traps, so every encoding in the range behaves identically regardless of the
    /// specific bit pattern.</summary>
    private int RaiseLineATrap()
    {
        RaiseException(10);
        return 34;
    }

    /// <summary>Line F ("Line 1111 Emulator") — vector 11, 34 cycles. Same deal as <see
    /// cref="RaiseLineATrap"/> but for the 0xF000-0xFFFF range, originally reserved for
    /// 68881/68882 floating-point coprocessor instructions the Genesis's plain 68000 never had.
    /// Some ROMs deliberately execute one of these as an anti-piracy/emulator-detection check —
    /// expecting a real trap into their own vector 11 handler, not a crash — so this needs to
    /// behave exactly like real hardware rather than treating the whole line as unimplemented.</summary>
    private int RaiseLineFTrap()
    {
        RaiseException(11);
        return 34;
    }

    /// <summary>CHK — bounds-checks Dn's low word against the range [0, EA]. Traps via
    /// vector 6 if Dn is negative or greater than the bound. N is set on the negative case
    /// and cleared on the too-large case (a real, if slightly odd, documented quirk); Z/V/C
    /// are left alone either way, matching "undefined" in the manual.</summary>
    private int ExecuteChk(ushort opcode)
    {
        int reg = (opcode >> 9) & 7;
        int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Word);

        short upperBound = unchecked((short)ReadEa(ea, Size.Word));
        short value = unchecked((short)(D[reg] & 0xFFFF));

        if (value < 0)
        {
            FlagNegative = true;
            RaiseException(6);
            return 40 + cycles;
        }

        if (value > upperBound)
        {
            FlagNegative = false;
            RaiseException(6);
            return 40 + cycles;
        }

        return 10 + cycles;
    }
}
