namespace GenesisSharp.CpuSh2;

/// <summary>Top-level opcode dispatch. Structure confirmed against PicoDrive's
/// cpu/sh2/mame/sh2pico.c:150-168 and cpu/sh2/mame/sh2.c's op0000-op1111 functions: an outer
/// switch on the opcode's top nibble to one of 16 handlers; nibbles that map to exactly one
/// instruction regardless of the remaining bits call it directly (e.g. op0111 = ADDI always),
/// nibbles that pack multiple instructions do their own inner switch on the low bits (or, for
/// 0x8/0xC, on bits 8-11 specifically) — matching PicoDrive's own mix exactly, not a
/// from-scratch redesign.
///
/// Every <c>case</c> below that PicoDrive itself decodes as ILLEGAL calls
/// <see cref="RaiseIllegalInstruction"/> — the real hardware behavior for that bit pattern. Every
/// other case implements the real instruction PicoDrive decodes there. The full standard SH-2
/// opcode set is covered (no on-chip cache/DMAC/timer/serial peripherals — those are bus/
/// peripheral concerns for a future 32X integration layer, not CPU-core concerns, consistent with
/// how VDP/YM2612/PSG are kept separate from M68000/Z80 elsewhere in this codebase).</summary>
public sealed partial class Sh2
{
    private int Execute(ushort opcode)
    {
        return (opcode >> 12) switch
        {
            0b0000 => ExecuteGroup0(opcode),
            0b0001 => ExecuteMovLS4((opcode >> 4) & 0xF, opcode & 0xF, (opcode >> 8) & 0xF),
            0b0010 => ExecuteGroup2(opcode),
            0b0011 => ExecuteGroup3(opcode),
            0b0100 => ExecuteGroup4(opcode),
            0b0101 => ExecuteMovLL4((opcode >> 4) & 0xF, opcode & 0xF, (opcode >> 8) & 0xF),
            0b0110 => ExecuteGroup6(opcode),
            0b0111 => ExecuteAddImmediate(opcode & 0xFF, (opcode >> 8) & 0xF),
            0b1000 => ExecuteGroup8(opcode),
            0b1001 => ExecuteMovWI(opcode & 0xFF, (opcode >> 8) & 0xF),
            0b1010 => ExecuteBra(opcode & 0xFFF),
            0b1011 => ExecuteBsr(opcode & 0xFFF),
            0b1100 => ExecuteGroupC(opcode),
            0b1101 => ExecuteMovLI(opcode & 0xFF, (opcode >> 8) & 0xF),
            0b1110 => ExecuteMovImmediate(opcode & 0xFF, (opcode >> 8) & 0xF),
            _ /* 0b1111 */ => RaiseIllegalInstruction(),
        };
    }

    /// <summary>Top nibble 0000. cpu/sh2/mame/sh2.c:1841-1925.</summary>
    private int ExecuteGroup0(ushort opcode)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        switch (opcode & 0x3F)
        {
            case 0x02: return ExecuteStcSr(n);
            case 0x03: return ExecuteBsrf(n); // dispatched as Rn, not Rm — confirmed sh2.c:1861
            case 0x04: case 0x14: case 0x24: case 0x34: return ExecuteMovBS0(m, n);
            case 0x05: case 0x15: case 0x25: case 0x35: return ExecuteMovWS0(m, n);
            case 0x06: case 0x16: case 0x26: case 0x36: return ExecuteMovLS0(m, n);
            case 0x08: return ExecuteClrt();
            case 0x09: return ExecuteNop();
            case 0x0A: return ExecuteStsMach(n);
            case 0x0B: return ExecuteRts();
            case 0x07: case 0x17: case 0x27: case 0x37: return ExecuteMull(m, n);
            case 0x0C: case 0x1C: case 0x2C: case 0x3C: return ExecuteMovBL0(m, n);
            case 0x0D: case 0x1D: case 0x2D: case 0x3D: return ExecuteMovWL0(m, n);
            case 0x0E: case 0x1E: case 0x2E: case 0x3E: return ExecuteMovLL0(m, n);
            case 0x0F: case 0x1F: case 0x2F: case 0x3F: return ExecuteMacL(m, n);
            case 0x12: return ExecuteStcGbr(n);
            case 0x18: return ExecuteSett();
            case 0x19: return ExecuteDiv0u();
            case 0x1A: return ExecuteStsMacl(n);
            case 0x1B: return ExecuteSleep();
            case 0x22: return ExecuteStcVbr(n);
            case 0x23: return ExecuteBraf(n); // dispatched as Rn — confirmed sh2.c:1895
            case 0x28: return ExecuteClrmac();
            case 0x29: return ExecuteMovt(n);
            case 0x2A: return ExecuteStsPr(n);
            case 0x2B: return ExecuteRte();

            case 0x00: case 0x01: case 0x10: case 0x11: case 0x13:
            case 0x20: case 0x21: case 0x30: case 0x31: case 0x32: case 0x33:
            case 0x38: case 0x39: case 0x3A: case 0x3B:
                return RaiseIllegalInstruction();

            default: return RaiseIllegalInstruction(); // unreachable: 0x00-0x3F all handled above
        }
    }

    /// <summary>Top nibble 0010. cpu/sh2/mame/sh2.c:1934-1955.</summary>
    private int ExecuteGroup2(ushort opcode)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        return (opcode & 0xF) switch
        {
            0x0 => ExecuteMovBS(m, n),
            0x1 => ExecuteMovWS(m, n),
            0x2 => ExecuteMovLS(m, n),
            0x3 => RaiseIllegalInstruction(),
            0x4 => ExecuteMovBM(m, n),
            0x5 => ExecuteMovWM(m, n),
            0x6 => ExecuteMovLM(m, n),
            0x8 => ExecuteTst(m, n),
            0x7 => ExecuteDiv0s(m, n),
            0x9 => ExecuteAnd(m, n),
            0xA => ExecuteXor(m, n),
            0xB => ExecuteOr(m, n),
            0xC => ExecuteCmpStr(m, n),
            0xD => ExecuteXtrct(m, n),
            0xE => ExecuteMulu(m, n),
            0xF => ExecuteMuls(m, n),
            _ => RaiseIllegalInstruction(), // unreachable: 0x0-0xF all handled above
        };
    }

    /// <summary>Top nibble 0011. cpu/sh2/mame/sh2.c:1957-1978.</summary>
    private int ExecuteGroup3(ushort opcode)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        return (opcode & 0xF) switch
        {
            0x0 => ExecuteCmpEq(m, n),
            0x1 => RaiseIllegalInstruction(),
            0x2 => ExecuteCmpHs(m, n),
            0x3 => ExecuteCmpGe(m, n),
            0x4 => ExecuteDiv1(m, n),
            0x5 => ExecuteDmulu(m, n),
            0x6 => ExecuteCmpHi(m, n),
            0x7 => ExecuteCmpGt(m, n),
            0x8 => ExecuteSub(m, n),
            0x9 => RaiseIllegalInstruction(),
            0xA => ExecuteSubc(m, n),
            0xB => ExecuteSubv(m, n),
            0xC => ExecuteAdd(m, n),
            0xD => ExecuteDmuls(m, n),
            0xE => ExecuteAddc(m, n),
            0xF => ExecuteAddv(m, n),
            _ => RaiseIllegalInstruction(), // unreachable: 0x0-0xF all handled above
        };
    }

    /// <summary>Top nibble 0100. cpu/sh2/mame/sh2.c:1980-2051 (the 0x30-0x3e ILLEGAL run and
    /// 0x3f MAC.W are listed out of numeric order in the source; reproduced in the same
    /// groupings here for easy comparison).</summary>
    private int ExecuteGroup4(ushort opcode)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF; // only MAC.W (0x0F/0x1F/0x2F/0x3F below) uses this

        switch (opcode & 0x3F)
        {
            case 0x00: return ExecuteShll(n);
            case 0x01: return ExecuteShlr(n);
            case 0x02: return ExecuteStsMMach(n);
            case 0x03: return ExecuteStcMSr(n);
            case 0x04: return ExecuteRotl(n);
            case 0x05: return ExecuteRotr(n);
            case 0x06: return ExecuteLdsMMach(n);
            case 0x07: return ExecuteLdcMSr(n);
            case 0x08: return ExecuteShll2(n);
            case 0x09: return ExecuteShlr2(n);
            case 0x0A: return ExecuteLdsMach(n);
            case 0x0B: return ExecuteJsr(n);
            case 0x0E: return ExecuteLdcSr(n);
            case 0x10: return ExecuteDt(n);
            case 0x11: return ExecuteCmpPz(n);
            case 0x12: return ExecuteStsMMacl(n);
            case 0x13: return ExecuteStcMGbr(n);
            case 0x15: return ExecuteCmpPl(n);
            case 0x16: return ExecuteLdsMMacl(n);
            case 0x17: return ExecuteLdcMGbr(n);
            case 0x18: return ExecuteShll8(n);
            case 0x19: return ExecuteShlr8(n);
            case 0x1A: return ExecuteLdsMacl(n);
            case 0x1B: return ExecuteTas(n);
            case 0x1E: return ExecuteLdcGbr(n);
            case 0x20: return ExecuteShal(n);
            case 0x21: return ExecuteShar(n);
            case 0x22: return ExecuteStsMPr(n);
            case 0x23: return ExecuteStcMVbr(n);
            case 0x24: return ExecuteRotcl(n);
            case 0x25: return ExecuteRotcr(n);
            case 0x26: return ExecuteLdsMPr(n);
            case 0x27: return ExecuteLdcMVbr(n);
            case 0x28: return ExecuteShll16(n);
            case 0x29: return ExecuteShlr16(n);
            case 0x2A: return ExecuteLdsPr(n);
            case 0x2B: return ExecuteJmp(n);
            case 0x2E: return ExecuteLdcVbr(n);
            case 0x0F: case 0x1F: case 0x2F: case 0x3F: return ExecuteMacW(m, n);

            case 0x0C: case 0x0D: case 0x14: case 0x1C: case 0x1D:
            case 0x2C: case 0x2D:
            case 0x30: case 0x31: case 0x32: case 0x33: case 0x34: case 0x35:
            case 0x36: case 0x37: case 0x38: case 0x39: case 0x3A: case 0x3B:
            case 0x3C: case 0x3D: case 0x3E:
                return RaiseIllegalInstruction();

            default: return RaiseIllegalInstruction(); // unreachable: 0x00-0x3F all handled above
        }
    }

    /// <summary>Top nibble 0110. cpu/sh2/mame/sh2.c:2060-2082.</summary>
    private int ExecuteGroup6(ushort opcode)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        return (opcode & 0xF) switch
        {
            0x0 => ExecuteMovBL(m, n),
            0x1 => ExecuteMovWL(m, n),
            0x2 => ExecuteMovLL(m, n),
            0x3 => ExecuteMov(m, n),
            0x4 => ExecuteMovBP(m, n),
            0x5 => ExecuteMovWP(m, n),
            0x6 => ExecuteMovLP(m, n),
            0x7 => ExecuteNot(m, n),
            0x8 => ExecuteSwapB(m, n),
            0x9 => ExecuteSwapW(m, n),
            0xA => ExecuteNegc(m, n),
            0xB => ExecuteNeg(m, n),
            0xC => ExecuteExtuB(m, n),
            0xD => ExecuteExtuW(m, n),
            0xE => ExecuteExtsB(m, n),
            0xF => ExecuteExtsW(m, n),
            _ => RaiseIllegalInstruction(), // unreachable: 0x0-0xF all handled above
        };
    }

    /// <summary>Top nibble 1000, sub-dispatched on bits 8-11 (not a register field here — the
    /// whole nibble's identity already fixes "1000", so this range instead selects the
    /// sub-instruction, leaving bits 4-7 as the one register operand). cpu/sh2/mame/
    /// sh2.c:2090-2111.</summary>
    private int ExecuteGroup8(ushort opcode)
    {
        int regM = (opcode >> 4) & 0xF;
        int disp4 = opcode & 0xF;
        int disp8 = opcode & 0xFF;

        return ((opcode >> 8) & 0xF) switch
        {
            0x0 => ExecuteMovBS4(disp4, regM),
            0x1 => ExecuteMovWS4(disp4, regM),
            0x4 => ExecuteMovBL4(regM, disp4),
            0x5 => ExecuteMovWL4(regM, disp4),
            0x8 => ExecuteCmpImmediate(disp8),
            0x9 => ExecuteBt(disp8),
            0xB => ExecuteBf(disp8),
            0xD => ExecuteBts(disp8),
            0xF => ExecuteBfs(disp8),
            _ => RaiseIllegalInstruction(), // 0x2/0x3/0x6/0x7/0xA/0xC/0xE — all ILLEGAL in this nibble
        };
    }

    /// <summary>Top nibble 1100, sub-dispatched on bits 8-11. cpu/sh2/mame/sh2.c:2133-2155.</summary>
    private int ExecuteGroupC(ushort opcode)
    {
        int disp8 = opcode & 0xFF;

        return ((opcode >> 8) & 0xF) switch
        {
            0x0 => ExecuteMovBSG(disp8),
            0x1 => ExecuteMovWSG(disp8),
            0x2 => ExecuteMovLSG(disp8),
            0x3 => ExecuteTrapa(disp8),
            0x4 => ExecuteMovBLG(disp8),
            0x5 => ExecuteMovWLG(disp8),
            0x6 => ExecuteMovLLG(disp8),
            0x7 => ExecuteMova(disp8),
            0x8 => ExecuteTstImmediate(disp8),
            0x9 => ExecuteAndImmediate(disp8),
            0xA => ExecuteXorImmediate(disp8),
            0xB => ExecuteOrImmediate(disp8),
            0xC => ExecuteTstGbr(disp8),
            0xD => ExecuteAndGbr(disp8),
            0xE => ExecuteXorGbr(disp8),
            _ => ExecuteOrGbr(disp8), // 0xF
        };
    }
}
