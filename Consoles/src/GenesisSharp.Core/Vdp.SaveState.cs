namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    /// <summary>Writes every field a save state needs to resume this VDP exactly where it left
    /// off: the three memory arrays, all 24 registers, the port protocol's mid-sequence latch
    /// state, DMA/FIFO timing bookkeeping, and scanline/interrupt timing. Doesn't include <see
    /// cref="FrameBuffer"/> -- it's pure rendered output, fully reproduced by the next <see
    /// cref="RenderScanline"/> call from the state that *is* saved here, not an input to
    /// anything.</summary>
    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Vram);
        foreach (ushort c in Cram) writer.Write(c);
        foreach (ushort v in Vsram) writer.Write(v);
        writer.Write(Registers);

        writer.Write(_pendingCommandWord.HasValue);
        if (_pendingCommandWord.HasValue) writer.Write(_pendingCommandWord.Value);
        writer.Write(_address);
        writer.Write(_code);
        writer.Write(_dmaFillPending);

        writer.Write(_slotBudget);
        writer.Write(_lastSlotClockCycles);
        writer.Write(_pendingStallCycles);
        writer.Write(_dmaBusyUntilCycle);
        writer.Write(TotalStallCyclesCharged);

        writer.Write(CurrentFieldIsOdd);

        writer.Write(CurrentScanline);
        writer.Write(_verticalInterruptPending);
        writer.Write(_spriteOverflowPending);
        writer.Write(_spriteCollisionPending);
        writer.Write(_hInterruptCountdown);
        writer.Write(ScanlineProgress);
    }

    public void LoadState(BinaryReader reader)
    {
        SaveStateIo.ReadExactly(reader, Vram);
        for (int i = 0; i < Cram.Length; i++) Cram[i] = reader.ReadUInt16();
        for (int i = 0; i < Vsram.Length; i++) Vsram[i] = reader.ReadUInt16();
        SaveStateIo.ReadExactly(reader, Registers);

        _pendingCommandWord = reader.ReadBoolean() ? reader.ReadUInt16() : null;
        _address = reader.ReadUInt32();
        _code = reader.ReadInt32();
        _dmaFillPending = reader.ReadBoolean();

        _slotBudget = reader.ReadDouble();
        _lastSlotClockCycles = reader.ReadInt64();
        _pendingStallCycles = reader.ReadInt32();
        _dmaBusyUntilCycle = reader.ReadInt64();
        TotalStallCyclesCharged = reader.ReadInt64();

        CurrentFieldIsOdd = reader.ReadBoolean();

        CurrentScanline = reader.ReadInt32();
        _verticalInterruptPending = reader.ReadBoolean();
        _spriteOverflowPending = reader.ReadBoolean();
        _spriteCollisionPending = reader.ReadBoolean();
        _hInterruptCountdown = reader.ReadInt32();
        ScanlineProgress = reader.ReadDouble();
    }
}
