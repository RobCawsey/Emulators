namespace NesSharp.Cpu;

/// <summary>
/// Bits of the 6502 status register (P). "Break" and "Unused" only ever exist as the
/// value pushed to the stack by BRK/PHP/interrupts — there is no real Break flag latch.
/// </summary>
[Flags]
public enum CpuFlags : byte
{
    None = 0,
    Carry = 1 << 0,
    Zero = 1 << 1,
    InterruptDisable = 1 << 2,
    Decimal = 1 << 3,
    Break = 1 << 4,
    Unused = 1 << 5,
    Overflow = 1 << 6,
    Negative = 1 << 7,
}
