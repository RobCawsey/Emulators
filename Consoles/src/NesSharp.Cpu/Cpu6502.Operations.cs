namespace NesSharp.Cpu;

/// <summary>
/// The actual operation behind each opcode: register/flag effects only. These are static
/// methods matching the ReadOp/WriteOp/ModifyOp/ImpliedOp/BranchCondition delegate shapes in
/// Opcodes.cs, kept separate from the cycle-timing machinery in Cpu6502.Templates.cs.
/// Binary-mode arithmetic only — the 2A03 in the NES carries the Decimal flag bit but its
/// ALU never acts on it, so BCD correction is intentionally not implemented.
/// </summary>
public sealed partial class Cpu6502
{
    internal static void OpLDA(Cpu6502 cpu, byte v) { cpu.A = v; cpu.SetZN(v); }
    internal static void OpLDX(Cpu6502 cpu, byte v) { cpu.X = v; cpu.SetZN(v); }
    internal static void OpLDY(Cpu6502 cpu, byte v) { cpu.Y = v; cpu.SetZN(v); }

    internal static byte OpSTA(Cpu6502 cpu) => cpu.A;
    internal static byte OpSTX(Cpu6502 cpu) => cpu.X;
    internal static byte OpSTY(Cpu6502 cpu) => cpu.Y;

    internal static void OpADC(Cpu6502 cpu, byte v) => cpu.Adc(v);
    internal static void OpSBC(Cpu6502 cpu, byte v) => cpu.Adc((byte)~v);

    internal static void OpAND(Cpu6502 cpu, byte v) { cpu.A &= v; cpu.SetZN(cpu.A); }
    internal static void OpORA(Cpu6502 cpu, byte v) { cpu.A |= v; cpu.SetZN(cpu.A); }
    internal static void OpEOR(Cpu6502 cpu, byte v) { cpu.A ^= v; cpu.SetZN(cpu.A); }

    internal static void OpCMP(Cpu6502 cpu, byte v) => cpu.Compare(cpu.A, v);
    internal static void OpCPX(Cpu6502 cpu, byte v) => cpu.Compare(cpu.X, v);
    internal static void OpCPY(Cpu6502 cpu, byte v) => cpu.Compare(cpu.Y, v);

    internal static void OpBIT(Cpu6502 cpu, byte v)
    {
        cpu.SetFlag(CpuFlags.Zero, (cpu.A & v) == 0);
        cpu.SetFlag(CpuFlags.Negative, (v & 0x80) != 0);
        cpu.SetFlag(CpuFlags.Overflow, (v & 0x40) != 0);
    }

    internal static byte OpASL(Cpu6502 cpu, byte v)
    {
        cpu.SetFlag(CpuFlags.Carry, (v & 0x80) != 0);
        byte r = (byte)(v << 1);
        cpu.SetZN(r);
        return r;
    }

    internal static byte OpLSR(Cpu6502 cpu, byte v)
    {
        cpu.SetFlag(CpuFlags.Carry, (v & 0x01) != 0);
        byte r = (byte)(v >> 1);
        cpu.SetZN(r);
        return r;
    }

    internal static byte OpROL(Cpu6502 cpu, byte v)
    {
        bool carryIn = cpu.GetFlag(CpuFlags.Carry);
        cpu.SetFlag(CpuFlags.Carry, (v & 0x80) != 0);
        byte r = (byte)((v << 1) | (carryIn ? 1 : 0));
        cpu.SetZN(r);
        return r;
    }

    internal static byte OpROR(Cpu6502 cpu, byte v)
    {
        bool carryIn = cpu.GetFlag(CpuFlags.Carry);
        cpu.SetFlag(CpuFlags.Carry, (v & 0x01) != 0);
        byte r = (byte)((v >> 1) | (carryIn ? 0x80 : 0));
        cpu.SetZN(r);
        return r;
    }

    internal static byte OpINC(Cpu6502 cpu, byte v) { byte r = (byte)(v + 1); cpu.SetZN(r); return r; }
    internal static byte OpDEC(Cpu6502 cpu, byte v) { byte r = (byte)(v - 1); cpu.SetZN(r); return r; }

    internal static void OpINX(Cpu6502 cpu) { cpu.X++; cpu.SetZN(cpu.X); }
    internal static void OpDEX(Cpu6502 cpu) { cpu.X--; cpu.SetZN(cpu.X); }
    internal static void OpINY(Cpu6502 cpu) { cpu.Y++; cpu.SetZN(cpu.Y); }
    internal static void OpDEY(Cpu6502 cpu) { cpu.Y--; cpu.SetZN(cpu.Y); }

    internal static void OpTAX(Cpu6502 cpu) { cpu.X = cpu.A; cpu.SetZN(cpu.X); }
    internal static void OpTXA(Cpu6502 cpu) { cpu.A = cpu.X; cpu.SetZN(cpu.A); }
    internal static void OpTAY(Cpu6502 cpu) { cpu.Y = cpu.A; cpu.SetZN(cpu.Y); }
    internal static void OpTYA(Cpu6502 cpu) { cpu.A = cpu.Y; cpu.SetZN(cpu.A); }
    internal static void OpTSX(Cpu6502 cpu) { cpu.X = cpu.SP; cpu.SetZN(cpu.X); }
    internal static void OpTXS(Cpu6502 cpu) { cpu.SP = cpu.X; }

    internal static void OpCLC(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.Carry, false);
    internal static void OpSEC(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.Carry, true);
    internal static void OpCLI(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.InterruptDisable, false);
    internal static void OpSEI(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.InterruptDisable, true);
    internal static void OpCLV(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.Overflow, false);
    internal static void OpCLD(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.Decimal, false);
    internal static void OpSED(Cpu6502 cpu) => cpu.SetFlag(CpuFlags.Decimal, true);
    internal static void OpNOP(Cpu6502 cpu) { }

    internal static bool CondBCC(Cpu6502 cpu) => !cpu.GetFlag(CpuFlags.Carry);
    internal static bool CondBCS(Cpu6502 cpu) => cpu.GetFlag(CpuFlags.Carry);
    internal static bool CondBEQ(Cpu6502 cpu) => cpu.GetFlag(CpuFlags.Zero);
    internal static bool CondBNE(Cpu6502 cpu) => !cpu.GetFlag(CpuFlags.Zero);
    internal static bool CondBMI(Cpu6502 cpu) => cpu.GetFlag(CpuFlags.Negative);
    internal static bool CondBPL(Cpu6502 cpu) => !cpu.GetFlag(CpuFlags.Negative);
    internal static bool CondBVC(Cpu6502 cpu) => !cpu.GetFlag(CpuFlags.Overflow);
    internal static bool CondBVS(Cpu6502 cpu) => cpu.GetFlag(CpuFlags.Overflow);

    // ---- Undocumented/illegal opcodes (the "stable" subset — combos of two documented
    // internal operations that behave consistently across NMOS 6502/2A03 chip revisions).
    // The unstable, analog-dependent ones (ANE/XAA, LXA, SHA, SHX, SHY, TAS, LAS) are
    // intentionally left unimplemented; see Opcodes.cs for which opcode bytes those are. ----

    internal static void OpLAX(Cpu6502 cpu, byte v) { cpu.A = v; cpu.X = v; cpu.SetZN(v); }

    internal static byte OpSAX(Cpu6502 cpu) => (byte)(cpu.A & cpu.X);

    internal static byte OpDCP(Cpu6502 cpu, byte v)
    {
        byte r = (byte)(v - 1);
        cpu.Compare(cpu.A, r);
        return r;
    }

    internal static byte OpISC(Cpu6502 cpu, byte v)
    {
        byte r = (byte)(v + 1);
        cpu.Adc((byte)~r);
        return r;
    }

    internal static byte OpSLO(Cpu6502 cpu, byte v)
    {
        cpu.SetFlag(CpuFlags.Carry, (v & 0x80) != 0);
        byte r = (byte)(v << 1);
        cpu.A |= r;
        cpu.SetZN(cpu.A);
        return r;
    }

    internal static byte OpRLA(Cpu6502 cpu, byte v)
    {
        bool carryIn = cpu.GetFlag(CpuFlags.Carry);
        cpu.SetFlag(CpuFlags.Carry, (v & 0x80) != 0);
        byte r = (byte)((v << 1) | (carryIn ? 1 : 0));
        cpu.A &= r;
        cpu.SetZN(cpu.A);
        return r;
    }

    internal static byte OpSRE(Cpu6502 cpu, byte v)
    {
        cpu.SetFlag(CpuFlags.Carry, (v & 0x01) != 0);
        byte r = (byte)(v >> 1);
        cpu.A ^= r;
        cpu.SetZN(cpu.A);
        return r;
    }

    internal static byte OpRRA(Cpu6502 cpu, byte v)
    {
        bool carryIn = cpu.GetFlag(CpuFlags.Carry);
        cpu.SetFlag(CpuFlags.Carry, (v & 0x01) != 0);
        byte r = (byte)((v >> 1) | (carryIn ? 0x80 : 0));
        cpu.Adc(r); // ADC uses the carry ROR just produced above
        return r;
    }

    /// <summary>The "DOP"/"TOP" NOP family: reads and discards an operand through whichever
    /// addressing mode the opcode byte specifies, otherwise behaves exactly like NOP.</summary>
    internal static void OpNopRead(Cpu6502 cpu, byte v) { }

    internal static void OpANC(Cpu6502 cpu, byte v)
    {
        cpu.A &= v;
        cpu.SetZN(cpu.A);
        cpu.SetFlag(CpuFlags.Carry, (cpu.A & 0x80) != 0);
    }

    internal static void OpALR(Cpu6502 cpu, byte v)
    {
        cpu.A &= v;
        cpu.SetFlag(CpuFlags.Carry, (cpu.A & 0x01) != 0);
        cpu.A = (byte)(cpu.A >> 1);
        cpu.SetZN(cpu.A);
    }

    internal static void OpARR(Cpu6502 cpu, byte v)
    {
        cpu.A &= v;
        bool carryIn = cpu.GetFlag(CpuFlags.Carry);
        cpu.A = (byte)((cpu.A >> 1) | (carryIn ? 0x80 : 0));
        cpu.SetZN(cpu.A);
        cpu.SetFlag(CpuFlags.Carry, (cpu.A & 0x40) != 0);
        cpu.SetFlag(CpuFlags.Overflow, (((cpu.A >> 6) ^ (cpu.A >> 5)) & 1) != 0);
    }

    internal static void OpSBX(Cpu6502 cpu, byte v)
    {
        int anded = cpu.A & cpu.X;
        cpu.SetFlag(CpuFlags.Carry, anded >= v);
        cpu.X = (byte)(anded - v);
        cpu.SetZN(cpu.X);
    }

    private void Adc(byte v)
    {
        int carryIn = GetFlag(CpuFlags.Carry) ? 1 : 0;
        int sum = A + v + carryIn;
        bool overflow = (~(A ^ v) & (A ^ sum) & 0x80) != 0;
        SetFlag(CpuFlags.Carry, sum > 0xFF);
        SetFlag(CpuFlags.Overflow, overflow);
        A = (byte)sum;
        SetZN(A);
    }

    private void Compare(byte register, byte v)
    {
        int r = register - v;
        SetFlag(CpuFlags.Carry, register >= v);
        SetZN((byte)r);
    }
}
