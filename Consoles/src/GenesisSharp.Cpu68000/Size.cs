namespace GenesisSharp.Cpu68000;

public enum Size
{
    Byte = 1,
    Word = 2,
    Long = 4,
}

internal static class SizeExtensions
{
    public static int Bits(this Size size) => (int)size * 8;
    public static uint SignBit(this Size size) => 1u << (size.Bits() - 1);
    public static uint Mask(this Size size) => size == Size.Long ? 0xFFFF_FFFFu : (1u << size.Bits()) - 1;

    public static uint SignExtend(this Size size, uint value)
    {
        if (size == Size.Long)
        {
            return value;
        }

        uint sign = size.SignBit();
        uint masked = value & size.Mask();
        return (masked & sign) != 0 ? masked | ~size.Mask() : masked;
    }
}
