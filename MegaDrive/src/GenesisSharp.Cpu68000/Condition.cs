namespace GenesisSharp.Cpu68000;

/// <summary>The 4-bit condition code field used by Bcc/DBcc/Scc, in encoding order.</summary>
public enum Condition
{
    True = 0x0,
    False = 0x1,
    Higher = 0x2,
    LowerOrSame = 0x3,
    CarryClear = 0x4,
    CarrySet = 0x5,
    NotEqual = 0x6,
    Equal = 0x7,
    OverflowClear = 0x8,
    OverflowSet = 0x9,
    Plus = 0xA,
    Minus = 0xB,
    GreaterOrEqual = 0xC,
    LessThan = 0xD,
    GreaterThan = 0xE,
    LessOrEqual = 0xF,
}
