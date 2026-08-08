namespace GenesisSharp.CpuSh2;

/// <summary>The SH-2's view of the outside world: on the 32X this would be cartridge ROM, the
/// 32X's own SDRAM, the frame buffer, PWM/communication registers, and a window back into
/// 68000 space — wired up by whatever assembles the 32X hardware (out of scope for this core).
/// Widths follow SH-2 terminology (byte/word/longword = 8/16/32 bits).
///
/// Unlike the 68000's IBus, <paramref name="address"/> is a full 32 bits with no masking
/// assumption at this layer — the SH-2 has a genuine 32-bit external address bus. Which bits are
/// physically wired and how the classic SH-2 CS0-CS3 area split (cached vs. cache-through views
/// of the same physical memory, confirmed against PicoDrive's <c>pico/32x/memory.c</c> — see
/// ARCHITECTURE.md) decodes is entirely a console/32X-specific concern for the consumer of this
/// interface, not this core.
///
/// There is deliberately no port-I/O pair the way <c>CpuZ80.IBus</c> has one: the SH-2 has no
/// port instructions at all — every peripheral and memory access goes through these same
/// load/store methods over one unified address space (confirmed against PicoDrive's SH-2 core,
/// which has no port-read/write concept anywhere).</summary>
public interface IBus
{
    byte ReadByte(uint address);
    ushort ReadWord(uint address);
    uint ReadLong(uint address);

    void WriteByte(uint address, byte value);
    void WriteWord(uint address, ushort value);
    void WriteLong(uint address, uint value);
}
