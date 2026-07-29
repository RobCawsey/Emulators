namespace NesSharp.Cpu;

/// <summary>
/// Memory bus the CPU talks to. Every read/write the real 6502 performs — including
/// dummy reads/writes during address calculation and read-modify-write instructions —
/// must go through here, since on the NES those cycles can have observable side effects
/// (e.g. a dummy read of $2007 still advances the PPU address).
/// </summary>
public interface IBus
{
    byte Read(ushort address);
    void Write(ushort address, byte value);
}
