namespace GenesisSharp.Cpu68000;

public enum EaKind
{
    DataRegister,
    AddressRegister,
    Memory,
}

/// <summary>A decoded operand location. <see cref="Register"/> is the D/A register index for
/// register-direct kinds, or the register the memory address was computed from (informational
/// only) for memory kinds.</summary>
public readonly struct EffectiveAddress(EaKind kind, int register, uint address)
{
    public EaKind Kind { get; } = kind;
    public int Register { get; } = register;
    public uint Address { get; } = address;

    public static EffectiveAddress DataRegister(int register) => new(EaKind.DataRegister, register, 0);
    public static EffectiveAddress AddressRegister(int register) => new(EaKind.AddressRegister, register, 0);
    public static EffectiveAddress Memory(uint address) => new(EaKind.Memory, -1, address);
}
