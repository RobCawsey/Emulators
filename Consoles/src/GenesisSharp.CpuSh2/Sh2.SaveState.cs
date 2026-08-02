namespace GenesisSharp.CpuSh2;

public sealed partial class Sh2
{
    /// <summary>Writes every field a save state needs to resume this CPU exactly where it left
    /// off: all 16 general registers, PC/PR/GBR/VBR/MACH/MACL/SR, total elapsed cycles, and the
    /// pending-interrupt/NMI-latch state. Deliberately excludes <see cref="_inDelaySlot"/> —
    /// per <see cref="Step"/>'s doc comment, a delayed branch and its delay-slot instruction are
    /// always fully consumed within one <see cref="Step"/> call, so that flag is never
    /// observably true between calls, exactly the same reasoning the 68000 core's save state
    /// gives for having no mid-instruction state of its own.</summary>
    public void SaveState(BinaryWriter writer)
    {
        for (int i = 0; i < 16; i++) writer.Write(R[i]);
        writer.Write(PC);
        writer.Write(PR);
        writer.Write(GBR);
        writer.Write(VBR);
        writer.Write(MACH);
        writer.Write(MACL);
        writer.Write(SR);
        writer.Write(TotalCycles);
        writer.Write(PendingInterruptLevel);
        writer.Write(_pendingVectorNumber);
        writer.Write(NmiPending);
    }

    public void LoadState(BinaryReader reader)
    {
        for (int i = 0; i < 16; i++) R[i] = reader.ReadUInt32();
        PC = reader.ReadUInt32();
        PR = reader.ReadUInt32();
        GBR = reader.ReadUInt32();
        VBR = reader.ReadUInt32();
        MACH = reader.ReadUInt32();
        MACL = reader.ReadUInt32();
        SR = reader.ReadUInt32();
        TotalCycles = reader.ReadInt64();
        PendingInterruptLevel = reader.ReadInt32();
        _pendingVectorNumber = reader.ReadInt32();
        NmiPending = reader.ReadBoolean();
    }
}
