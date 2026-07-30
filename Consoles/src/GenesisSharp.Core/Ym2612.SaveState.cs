namespace GenesisSharp.Core;

public sealed partial class Ym2612
{
    /// <summary>Writes every field a save state needs to resume this chip exactly where it left
    /// off: both register banks, the DAC latch, both timers, and all 24 operators' phase/
    /// envelope/feedback-history state (<see cref="OperatorState"/> isn't itself
    /// register-derived -- phase and envelope position are continuously-advancing state a
    /// register dump alone can't reconstruct).</summary>
    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_part1Registers);
        writer.Write(_part2Registers);
        writer.Write(_part1Address);
        writer.Write(_part2Address);
        writer.Write(_dacEnabled);
        writer.Write(_dacSample);
        writer.Write(ClipCount);

        writer.Write(_timerACounter);
        writer.Write(_timerATickAccumulator);
        writer.Write(_timerAEnabled);
        writer.Write(_timerAOverflow);
        writer.Write(_timerBCounter);
        writer.Write(_timerBTickAccumulator);
        writer.Write(_timerBEnabled);
        writer.Write(_timerBOverflow);

        for (int channel = 0; channel < 6; channel++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                OperatorState op = _operators[channel][slot];
                writer.Write(op.Phase);
                writer.Write((int)op.Envelope);
                writer.Write(op.AttenuationDb);
                writer.Write(op.PreviousOutput1);
                writer.Write(op.PreviousOutput2);
                writer.Write(op.LastSampleOutput);
                writer.Write(op.KeyOn);
            }
        }
    }

    public void LoadState(BinaryReader reader)
    {
        SaveStateIo.ReadExactly(reader, _part1Registers);
        SaveStateIo.ReadExactly(reader, _part2Registers);
        _part1Address = reader.ReadByte();
        _part2Address = reader.ReadByte();
        _dacEnabled = reader.ReadBoolean();
        _dacSample = reader.ReadByte();
        ClipCount = reader.ReadInt64();

        _timerACounter = reader.ReadInt32();
        _timerATickAccumulator = reader.ReadDouble();
        _timerAEnabled = reader.ReadBoolean();
        _timerAOverflow = reader.ReadBoolean();
        _timerBCounter = reader.ReadInt32();
        _timerBTickAccumulator = reader.ReadDouble();
        _timerBEnabled = reader.ReadBoolean();
        _timerBOverflow = reader.ReadBoolean();

        for (int channel = 0; channel < 6; channel++)
        {
            for (int slot = 0; slot < 4; slot++)
            {
                OperatorState op = _operators[channel][slot];
                op.Phase = reader.ReadDouble();
                op.Envelope = (EnvelopePhase)reader.ReadInt32();
                op.AttenuationDb = reader.ReadDouble();
                op.PreviousOutput1 = reader.ReadDouble();
                op.PreviousOutput2 = reader.ReadDouble();
                op.LastSampleOutput = reader.ReadDouble();
                op.KeyOn = reader.ReadBoolean();
            }
        }
    }
}
