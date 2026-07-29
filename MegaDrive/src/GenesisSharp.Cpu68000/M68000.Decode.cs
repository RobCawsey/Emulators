namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Top-level decode: dispatches on the primary opcode nibble (bits 15-12), the
    /// same coarse grouping Motorola's own opcode map uses. Instruction groups not yet
    /// implemented (NEGX, address/bus error traps) throw NotImplementedException with a
    /// message naming the gap, rather than silently misbehaving.
    ///
    /// SBCD/ABCD/ADDX/SUBX/EXG all nest inside what would otherwise be the OR/AND/SUB/ADD
    /// register-op encoding (distinguished only by a carve-out in the EA-mode field); each is
    /// now checked explicitly before falling through to the "plain" instruction — see
    /// <see cref="Decode1000"/>, <see cref="Decode1001"/>, <see cref="Decode1100"/>,
    /// <see cref="Decode1101"/>. Similarly, MOVE-to-CCR and MOVE-to-SR nest inside what would
    /// otherwise be NEG's and NOT's "reserved size = 3" sub-range respectively — <see
    /// cref="Decode0100"/> checks those narrower masks first.
    /// </summary>
    private int Decode(ushort opcode)
    {
        return ((opcode >> 12) & 0xF) switch
        {
            0x0 => Decode0000(opcode),
            0x1 => ExecuteMove(opcode, Size.Byte),
            0x2 => ExecuteMove(opcode, Size.Long),
            0x3 => ExecuteMove(opcode, Size.Word),
            0x4 => Decode0100(opcode),
            0x5 => Decode0101(opcode),
            0x6 => ExecuteBranch(opcode),
            0x7 => (opcode & 0x0100) == 0 ? ExecuteMoveQuick(opcode) : throw Unimplemented(opcode, "reserved MOVEQ encoding"),
            0x8 => Decode1000(opcode),
            0x9 => Decode1001(opcode),
            0xA => RaiseLineATrap(),
            0xB => ExecuteCmpOrEor(opcode),
            0xC => Decode1100(opcode),
            0xD => Decode1101(opcode),
            0xE => Decode1110(opcode),
            0xF => RaiseLineFTrap(),
            _ => throw new InvalidOperationException("Unreachable."),
        };
    }

    private static NotImplementedException Unimplemented(ushort opcode, string what) =>
        new($"Opcode 0x{opcode:X4} ({what}) is not implemented yet.");

    private int Decode0000(ushort opcode)
    {
        int subNibble = (opcode >> 8) & 0xF;
        if ((subNibble & 1) == 1)
        {
            // EA mode 001 is reserved for MOVEP in this sub-space; every other EA mode here
            // is a dynamic bit op (bit number comes from the Dn named in bits 11-9).
            int eaMode = (opcode >> 3) & 7;
            return eaMode == 1 ? ExecuteMovep(opcode) : ExecuteDynamicBitOperation(opcode);
        }

        return subNibble switch
        {
            0x0 => ExecuteOrImmediate(opcode),
            0x2 => ExecuteAndImmediate(opcode),
            0x4 => ExecuteSubImmediate(opcode),
            0x6 => ExecuteAddImmediate(opcode),
            0x8 => ExecuteStaticBitOperation(opcode),
            0xA => ExecuteEorImmediate(opcode),
            0xC => ExecuteCmpImmediate(opcode),
            _ => RaiseIllegalInstruction(), // subNibble 0xE — architecturally reserved
        };
    }

    private int Decode0100(ushort opcode)
    {
        switch (opcode)
        {
            case 0x4E70: return ExecuteReset(opcode);
            case 0x4E71: return ExecuteNop(opcode);
            case 0x4E72: return ExecuteStop(opcode);
            case 0x4E73: return ExecuteRte(opcode);
            case 0x4E75: return ExecuteRts(opcode);
            case 0x4E76: return ExecuteTrapv(opcode);
            case 0x4E77: return ExecuteRtr(opcode);
            // Falls inside TAS's own 0x4AC0-0x4AFF opcode range (0xFFC0 mask below), but
            // Motorola carved this exact word out as the dedicated illegal-instruction opcode
            // rather than "TAS with an immediate/PC-relative operand" (which wouldn't make
            // sense anyway, since TAS needs a writable destination). Left to the general TAS
            // dispatch below, this decoded as TAS with EA mode 7/reg 4 (immediate), silently
            // consuming an extra word as a fake operand and resuming at the wrong address --
            // found via a real ROM (Sonic 1) placing a genuine 0x4AFC "should never execute"
            // trap in its code, which this made resume two words later, in the middle of
            // unrelated bytes, instead of trapping to vector 4.
            case 0x4AFC: return RaiseIllegalInstruction();
        }

        if ((opcode & 0xFFF8) == 0x4E50) return ExecuteLink(opcode);
        if ((opcode & 0xFFF8) == 0x4E58) return ExecuteUnlk(opcode);
        if ((opcode & 0xFFC0) == 0x4E80) return ExecuteJsr(opcode);
        if ((opcode & 0xFFC0) == 0x4EC0) return ExecuteJmp(opcode);
        if ((opcode & 0xFFF8) == 0x4840) return ExecuteSwap(opcode);
        if ((opcode & 0xFFF8) == 0x4880) return ExecuteExtWord(opcode);
        if ((opcode & 0xFFF8) == 0x48C0) return ExecuteExtLong(opcode);
        if ((opcode & 0xF1C0) == 0x41C0) return ExecuteLea(opcode);
        if ((opcode & 0xFFC0) == 0x4840) return ExecutePea(opcode);
        if ((opcode & 0xFF00) == 0x4200) return ExecuteClr(opcode);
        if ((opcode & 0xFFC0) == 0x40C0) return ExecuteMoveFromSr(opcode);
        if ((opcode & 0xFFC0) == 0x44C0) return ExecuteMoveToCcr(opcode);
        if ((opcode & 0xFFC0) == 0x46C0) return ExecuteMoveToSr(opcode);
        if ((opcode & 0xFF00) == 0x4400) return ExecuteNeg(opcode);
        if ((opcode & 0xFF00) == 0x4600) return ExecuteNot(opcode);
        if ((opcode & 0xFFC0) == 0x4AC0) return ExecuteTas(opcode);
        if ((opcode & 0xFF00) == 0x4A00) return ExecuteTst(opcode);
        if ((opcode & 0xFB80) == 0x4880) return ExecuteMovem(opcode);
        if ((opcode & 0xF1C0) == 0x4180) return ExecuteChk(opcode);
        if ((opcode & 0xFFF0) == 0x4E40) return ExecuteTrap(opcode);
        if ((opcode & 0xFFF0) == 0x4E60) return ExecuteMoveUsp(opcode);

        throw Unimplemented(opcode, "miscellaneous group 0100 (e.g. NEGX)");
    }

    private int Decode1000(ushort opcode) => IsSbcd(opcode) ? ExecuteSbcd(opcode) : ExecuteOr(opcode);

    private int Decode1001(ushort opcode) => IsSubx(opcode) ? ExecuteSubx(opcode) : ExecuteSub(opcode);

    private int Decode1100(ushort opcode) => IsExg(opcode) ? ExecuteExg(opcode) : IsAbcd(opcode) ? ExecuteAbcd(opcode) : ExecuteAnd(opcode);

    private int Decode1101(ushort opcode) => IsAddx(opcode) ? ExecuteAddx(opcode) : ExecuteAdd(opcode);

    private int Decode0101(ushort opcode)
    {
        if (((opcode >> 6) & 3) == 3)
        {
            int eaMode = (opcode >> 3) & 7, eaReg = opcode & 7;
            var condition = (Condition)((opcode >> 8) & 0xF);
            return eaMode == 1 ? ExecuteDbcc(condition, eaReg) : ExecuteScc(opcode, condition, eaMode, eaReg);
        }

        return (opcode & 0x0100) == 0 ? ExecuteAddQuick(opcode) : ExecuteSubQuick(opcode);
    }
}
