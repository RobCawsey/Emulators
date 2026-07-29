namespace GenesisSharp.Core;

public sealed partial class Psg
{
    /// <summary>Writes every field a save state needs to resume this chip exactly where it left
    /// off: the tone/volume/noise registers, the latch state a mid-sequence write pair depends
    /// on, and the continuously-advancing tone-phase/noise-LFSR state a register dump alone
    /// can't reconstruct.</summary>
    public void SaveState(BinaryWriter writer)
    {
        foreach (ushort f in ToneFrequency) writer.Write(f);
        writer.Write(Volume);
        writer.Write(NoiseControl);
        writer.Write(_latchedRegister);

        foreach (double p in _tonePhase) writer.Write(p);
        writer.Write(_noiseLfsr);
        writer.Write(_noisePhase);
    }

    public void LoadState(BinaryReader reader)
    {
        for (int i = 0; i < ToneFrequency.Length; i++) ToneFrequency[i] = reader.ReadUInt16();
        SaveStateIo.ReadExactly(reader, Volume);
        NoiseControl = reader.ReadByte();
        _latchedRegister = reader.ReadInt32();

        for (int i = 0; i < _tonePhase.Length; i++) _tonePhase[i] = reader.ReadDouble();
        _noiseLfsr = reader.ReadUInt32();
        _noisePhase = reader.ReadDouble();
    }
}
