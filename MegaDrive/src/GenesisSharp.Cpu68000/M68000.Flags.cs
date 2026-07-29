namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    private const ushort FlagC = 1 << 0;
    private const ushort FlagV = 1 << 1;
    private const ushort FlagZ = 1 << 2;
    private const ushort FlagN = 1 << 3;
    private const ushort FlagX = 1 << 4;
    private const ushort SupervisorBit = 1 << 13;

    public bool FlagCarry { get => (SR & FlagC) != 0; set => SetFlag(FlagC, value); }
    public bool FlagOverflow { get => (SR & FlagV) != 0; set => SetFlag(FlagV, value); }
    public bool FlagZero { get => (SR & FlagZ) != 0; set => SetFlag(FlagZ, value); }
    public bool FlagNegative { get => (SR & FlagN) != 0; set => SetFlag(FlagN, value); }
    public bool FlagExtend { get => (SR & FlagX) != 0; set => SetFlag(FlagX, value); }
    public bool Supervisor => (SR & SupervisorBit) != 0;

    private void SetFlag(ushort bit, bool value) => SR = value ? (ushort)(SR | bit) : (ushort)(SR & ~bit);

    private bool TestCondition(Condition condition) => condition switch
    {
        Condition.True => true,
        Condition.False => false,
        Condition.Higher => !FlagCarry && !FlagZero,
        Condition.LowerOrSame => FlagCarry || FlagZero,
        Condition.CarryClear => !FlagCarry,
        Condition.CarrySet => FlagCarry,
        Condition.NotEqual => !FlagZero,
        Condition.Equal => FlagZero,
        Condition.OverflowClear => !FlagOverflow,
        Condition.OverflowSet => FlagOverflow,
        Condition.Plus => !FlagNegative,
        Condition.Minus => FlagNegative,
        Condition.GreaterOrEqual => FlagNegative == FlagOverflow,
        Condition.LessThan => FlagNegative != FlagOverflow,
        Condition.GreaterThan => FlagNegative == FlagOverflow && !FlagZero,
        Condition.LessOrEqual => FlagNegative != FlagOverflow || FlagZero,
        _ => throw new ArgumentOutOfRangeException(nameof(condition)),
    };

    /// <summary>N/Z set from the result, V and C cleared, X unaffected — used by MOVE, AND, OR,
    /// EOR, NOT, TST, CLR, EXT, SWAP and friends.</summary>
    private void SetFlagsLogic(Size size, uint result)
    {
        uint masked = result & size.Mask();
        FlagNegative = (masked & size.SignBit()) != 0;
        FlagZero = masked == 0;
        FlagOverflow = false;
        FlagCarry = false;
    }

    /// <summary>Flags for dst + src = result (ADD/ADDI/ADDQ/ADDA-on-Dn/NEG's sibling ADD case).
    /// X mirrors C unless suppressed (not currently needed for the ADD family).</summary>
    private void SetFlagsAdd(Size size, uint src, uint dst, uint result)
    {
        uint mask = size.Mask();
        uint s = src & mask, d = dst & mask, r = result & mask;
        uint sign = size.SignBit();

        FlagNegative = (r & sign) != 0;
        FlagZero = r == 0;
        FlagOverflow = (((~d ^ s) & (d ^ r)) & sign) != 0;
        FlagCarry = ((ulong)d + s) > mask;
        FlagExtend = FlagCarry;
    }

    /// <summary>Flags for dst - src = result (SUB/SUBI/SUBQ/NEG). CMP uses the same math but
    /// leaves X untouched, so it goes through <see cref="SetFlagsCompare"/> instead.</summary>
    private void SetFlagsSub(Size size, uint src, uint dst, uint result)
    {
        SetFlagsSubCore(size, src, dst, result);
        FlagExtend = FlagCarry;
    }

    private void SetFlagsCompare(Size size, uint src, uint dst, uint result) => SetFlagsSubCore(size, src, dst, result);

    private void SetFlagsSubCore(Size size, uint src, uint dst, uint result)
    {
        uint mask = size.Mask();
        uint s = src & mask, d = dst & mask, r = result & mask;
        uint sign = size.SignBit();

        FlagNegative = (r & sign) != 0;
        FlagZero = r == 0;
        FlagOverflow = (((d ^ s) & (d ^ r)) & sign) != 0;
        FlagCarry = s > d;
    }
}
