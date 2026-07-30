namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Writes every field a save state needs to resume this CPU exactly where it left
    /// off: all 16 general registers, PC/SR, the shadow stack pointer not currently active in
    /// <see cref="A"/>[7] (<see cref="_usp"/>), total elapsed cycles, and the STOP/pending-
    /// interrupt state. Deliberately excludes anything that's only ever valid mid-<see
    /// cref="Step"/> (there is none here -- unlike the Z80's indexed-prefix decode state, this
    /// CPU decodes and executes an instruction atomically within one call), so it's safe to call
    /// between any two <see cref="Step"/> calls.</summary>
    public void SaveState(BinaryWriter writer)
    {
        for (int i = 0; i < 8; i++) writer.Write(D[i]);
        for (int i = 0; i < 8; i++) writer.Write(A[i]);
        writer.Write(PC);
        writer.Write(SR);
        writer.Write(_usp);
        writer.Write(TotalCycles);
        writer.Write(Stopped);
        writer.Write(PendingInterruptLevel);
    }

    public void LoadState(BinaryReader reader)
    {
        for (int i = 0; i < 8; i++) D[i] = reader.ReadUInt32();
        for (int i = 0; i < 8; i++) A[i] = reader.ReadUInt32();
        PC = reader.ReadUInt32();
        SR = reader.ReadUInt16();
        _usp = reader.ReadUInt32();
        TotalCycles = reader.ReadInt64();
        Stopped = reader.ReadBoolean();
        PendingInterruptLevel = reader.ReadInt32();
    }
}
