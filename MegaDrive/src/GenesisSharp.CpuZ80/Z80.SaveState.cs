namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    /// <summary>Writes every field a save state needs to resume this CPU exactly where it left
    /// off: both register sets (main + shadow), IX/IY/SP/PC, I/R, the interrupt enable
    /// flip-flops and mode, HALT state, elapsed cycles, and any interrupt latched but not yet
    /// serviced (<see cref="NmiPending"/>/<see cref="InterruptPending"/> can genuinely still be
    /// true at a frame boundary if IFF1 was disabled when it was raised). Deliberately excludes
    /// the indexed-prefix decode state (<c>_indexMode</c>/<c>_displacement</c>) -- those are only
    /// ever valid mid-<see cref="Step"/>, back to their default by the time any call between
    /// <see cref="Step"/> calls could observe them.</summary>
    public void SaveState(BinaryWriter writer)
    {
        writer.Write(A); writer.Write(F); writer.Write(B); writer.Write(C);
        writer.Write(D); writer.Write(E); writer.Write(H); writer.Write(L);
        writer.Write(AltA); writer.Write(AltF); writer.Write(AltB); writer.Write(AltC);
        writer.Write(AltD); writer.Write(AltE); writer.Write(AltH); writer.Write(AltL);
        writer.Write(IX);
        writer.Write(IY);
        writer.Write(SP);
        writer.Write(PC);
        writer.Write(I);
        writer.Write(R);
        writer.Write(Iff1);
        writer.Write(Iff2);
        writer.Write(InterruptMode);
        writer.Write(Halted);
        writer.Write(TotalCycles);
        writer.Write(NmiPending);
        writer.Write(InterruptPending);
        writer.Write(_interruptDataBusValue);
    }

    public void LoadState(BinaryReader reader)
    {
        A = reader.ReadByte(); F = reader.ReadByte(); B = reader.ReadByte(); C = reader.ReadByte();
        D = reader.ReadByte(); E = reader.ReadByte(); H = reader.ReadByte(); L = reader.ReadByte();
        AltA = reader.ReadByte(); AltF = reader.ReadByte(); AltB = reader.ReadByte(); AltC = reader.ReadByte();
        AltD = reader.ReadByte(); AltE = reader.ReadByte(); AltH = reader.ReadByte(); AltL = reader.ReadByte();
        IX = reader.ReadUInt16();
        IY = reader.ReadUInt16();
        SP = reader.ReadUInt16();
        PC = reader.ReadUInt16();
        I = reader.ReadByte();
        R = reader.ReadByte();
        Iff1 = reader.ReadBoolean();
        Iff2 = reader.ReadBoolean();
        InterruptMode = reader.ReadInt32();
        Halted = reader.ReadBoolean();
        TotalCycles = reader.ReadInt64();
        NmiPending = reader.ReadBoolean();
        InterruptPending = reader.ReadBoolean();
        _interruptDataBusValue = reader.ReadByte();
    }
}
