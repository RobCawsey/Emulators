namespace GenesisSharp.CpuZ80;

/// <summary>The Z80's view of the outside world: its own 8KB work RAM, the YM2612/SN76489
/// sound chips, and (when granted) a banked window into 68000 address space — wired up by
/// GenesisSharp.Core.</summary>
public interface IBus
{
    byte ReadByte(ushort address);
    void WriteByte(ushort address, byte value);

    byte ReadPort(byte port);
    void WritePort(byte port, byte value);
}
