namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    private const byte FlagCBit = 1 << 0;
    private const byte FlagNBit = 1 << 1;
    private const byte FlagPvBit = 1 << 2;
    private const byte FlagXBit = 1 << 3; // undocumented, mirrors bit 3 of the result
    private const byte FlagHBit = 1 << 4;
    private const byte FlagYBit = 1 << 5; // undocumented, mirrors bit 5 of the result
    private const byte FlagZBit = 1 << 6;
    private const byte FlagSBit = 1 << 7;

    public bool FlagCarry { get => (F & FlagCBit) != 0; set => SetFlag(FlagCBit, value); }
    public bool FlagSubtract { get => (F & FlagNBit) != 0; set => SetFlag(FlagNBit, value); }
    public bool FlagParityOverflow { get => (F & FlagPvBit) != 0; set => SetFlag(FlagPvBit, value); }
    public bool FlagHalfCarry { get => (F & FlagHBit) != 0; set => SetFlag(FlagHBit, value); }
    public bool FlagZero { get => (F & FlagZBit) != 0; set => SetFlag(FlagZBit, value); }
    public bool FlagSign { get => (F & FlagSBit) != 0; set => SetFlag(FlagSBit, value); }

    private void SetFlag(byte bit, bool value) => F = value ? (byte)(F | bit) : (byte)(F & ~bit);

    private static bool Parity(byte value) => (System.Numerics.BitOperations.PopCount(value) & 1) == 0;

    /// <summary>The undocumented X/Y flags mirror bits 3 and 5 of a "reference" byte — the
    /// result for most instructions. This is a best-effort implementation: real silicon uses
    /// slightly different reference bytes for a few corner cases (e.g. BIT n,(HL) uses an
    /// internal latch, not the tested byte), which this does not reproduce. No Genesis sound
    /// driver should be relying on that level of detail.</summary>
    private void SetUndocumentedFlags(byte reference)
    {
        F = (byte)((F & ~(FlagXBit | FlagYBit)) | (reference & (FlagXBit | FlagYBit)));
    }

    /// <summary>S/Z/PV(parity)/H(=0)/N(=0)/C(=0) for AND/OR/XOR.</summary>
    private void SetFlagsLogic(byte result)
    {
        FlagSign = (result & 0x80) != 0;
        FlagZero = result == 0;
        FlagParityOverflow = Parity(result);
        FlagHalfCarry = false;
        FlagSubtract = false;
        FlagCarry = false;
        SetUndocumentedFlags(result);
    }

    /// <summary>8-bit ADD/ADC: A = A + n (+ carry-in).</summary>
    private void SetFlagsAdd8(byte a, byte n, int carryIn, out byte result)
    {
        int sum = a + n + carryIn;
        byte r = (byte)sum;
        result = r;

        FlagSign = (r & 0x80) != 0;
        FlagZero = r == 0;
        FlagHalfCarry = ((a & 0xF) + (n & 0xF) + carryIn) > 0xF;
        FlagParityOverflow = ((a ^ n) & 0x80) == 0 && ((a ^ r) & 0x80) != 0;
        FlagSubtract = false;
        FlagCarry = sum > 0xFF;
        SetUndocumentedFlags(r);
    }

    /// <summary>8-bit SUB/SBC/CP: A - n (- carry-in). <paramref name="result"/> is the
    /// truncated difference regardless of whether the caller stores it (CP discards it).</summary>
    private void SetFlagsSub8(byte a, byte n, int carryIn, out byte result)
    {
        int diff = a - n - carryIn;
        byte r = (byte)diff;
        result = r;

        FlagSign = (r & 0x80) != 0;
        FlagZero = r == 0;
        FlagHalfCarry = (a & 0xF) < ((n & 0xF) + carryIn);
        FlagParityOverflow = ((a ^ n) & (a ^ r) & 0x80) != 0;
        FlagSubtract = true;
        FlagCarry = diff < 0;
        SetUndocumentedFlags(r);
    }

    /// <summary>INC r — unlike ADD, never touches C.</summary>
    private byte ApplyInc8(byte value)
    {
        byte result = (byte)(value + 1);
        FlagSign = (result & 0x80) != 0;
        FlagZero = result == 0;
        FlagHalfCarry = (value & 0xF) == 0xF;
        FlagParityOverflow = value == 0x7F;
        FlagSubtract = false;
        SetUndocumentedFlags(result);
        return result;
    }

    /// <summary>DEC r — unlike SUB, never touches C.</summary>
    private byte ApplyDec8(byte value)
    {
        byte result = (byte)(value - 1);
        FlagSign = (result & 0x80) != 0;
        FlagZero = result == 0;
        FlagHalfCarry = (value & 0xF) == 0x00;
        FlagParityOverflow = value == 0x80;
        FlagSubtract = true;
        SetUndocumentedFlags(result);
        return result;
    }

    /// <summary>ADD HL,rr (main table) — only H/N/C are affected; S/Z/PV are left alone. This
    /// is what distinguishes it from the ED-prefixed ADC/SBC HL,rr below.</summary>
    private ushort ApplyAdd16(ushort hl, ushort rr)
    {
        int sum = hl + rr;
        ushort result = (ushort)sum;
        FlagHalfCarry = ((hl & 0xFFF) + (rr & 0xFFF)) > 0xFFF;
        FlagSubtract = false;
        FlagCarry = sum > 0xFFFF;
        return result;
    }

    /// <summary>ADC HL,rr — affects every flag, unlike plain ADD HL,rr.</summary>
    private ushort ApplyAdc16(ushort hl, ushort rr)
    {
        int carryIn = FlagCarry ? 1 : 0;
        int sum = hl + rr + carryIn;
        ushort result = (ushort)sum;

        FlagSign = (result & 0x8000) != 0;
        FlagZero = result == 0;
        FlagHalfCarry = ((hl & 0xFFF) + (rr & 0xFFF) + carryIn) > 0xFFF;
        FlagParityOverflow = ((hl ^ rr) & 0x8000) == 0 && ((hl ^ result) & 0x8000) != 0;
        FlagSubtract = false;
        FlagCarry = sum > 0xFFFF;
        SetUndocumentedFlags((byte)(result >> 8));
        return result;
    }

    /// <summary>SBC HL,rr — affects every flag, mirroring <see cref="ApplyAdc16"/>.</summary>
    private ushort ApplySbc16(ushort hl, ushort rr)
    {
        int borrowIn = FlagCarry ? 1 : 0;
        int diff = hl - rr - borrowIn;
        ushort result = (ushort)diff;

        FlagSign = (result & 0x8000) != 0;
        FlagZero = result == 0;
        FlagHalfCarry = (hl & 0xFFF) < ((rr & 0xFFF) + borrowIn);
        FlagParityOverflow = ((hl ^ rr) & (hl ^ result) & 0x8000) != 0;
        FlagSubtract = true;
        FlagCarry = diff < 0;
        SetUndocumentedFlags((byte)(result >> 8));
        return result;
    }
}
