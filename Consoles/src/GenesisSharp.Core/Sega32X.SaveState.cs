namespace GenesisSharp.Core;

public sealed partial class Sega32X
{
    /// <summary>Writes every field a save state needs to resume the 32X subsystem exactly where
    /// it left off: the adapter/control register block, SDRAM, both boot ROMs (currently always
    /// blank during normal play, per Phase 2's known gaps — included anyway so a future
    /// boot-stub-synthesis phase doesn't silently need a save-state-version bump to start
    /// persisting them), both SH-2 cores' own state, the VDP overlay's registers/palette/frame
    /// buffer/blanking bookkeeping, and the PWM chip's FIFOs/phase. Matches the
    /// <c>Cpu.SaveState(writer)</c>-shape convention every other component in this project
    /// already follows (see <see cref="GenesisConsole.SaveState"/>).</summary>
    public void SaveState(BinaryWriter writer)
    {
        foreach (ushort r in Regs) writer.Write(r);
        writer.Write(Sdram);
        writer.Write(BootRomMaster);
        writer.Write(BootRomSlave);

        MasterSh2.SaveState(writer);
        SlaveSh2.SaveState(writer);
        writer.Write(_masterSh2Bus.PeripheralRegs);
        writer.Write(_slaveSh2Bus.PeripheralRegs);

        writer.Write(Sh2IrqMask[0]);
        writer.Write(Sh2IrqMask[1]);
        writer.Write(Sh2IrqPending[0]);
        writer.Write(Sh2IrqPending[1]);
        writer.Write(_hIntCounterReg);
        writer.Write(_hIntCountdown);

        foreach (ushort r in VdpRegs) writer.Write(r);
        foreach (ushort p in Palette) writer.Write(p);
        writer.Write(FrameBuffer[0]);
        writer.Write(FrameBuffer[1]);
        writer.Write(_wasVBlank);
        writer.Write(_pendingFrameSelectValue);
        writer.Write(_blankFakeCounter);

        foreach (ushort v in _pwmFifo[0]) writer.Write(v);
        foreach (ushort v in _pwmFifo[1]) writer.Write(v);
        writer.Write(_pwmFifoCount[0]);
        writer.Write(_pwmFifoCount[1]);
        writer.Write(_pwmFifoHead[0]);
        writer.Write(_pwmFifoHead[1]);
        writer.Write(_pwmCurrent[0]);
        writer.Write(_pwmCurrent[1]);
        writer.Write(_pwmCycleDebt);
        writer.Write(_pwmIrqCounter);
    }

    public void LoadState(BinaryReader reader)
    {
        for (int i = 0; i < Regs.Length; i++) Regs[i] = reader.ReadUInt16();
        SaveStateIo.ReadExactly(reader, Sdram);
        SaveStateIo.ReadExactly(reader, BootRomMaster);
        SaveStateIo.ReadExactly(reader, BootRomSlave);

        MasterSh2.LoadState(reader);
        SlaveSh2.LoadState(reader);
        SaveStateIo.ReadExactly(reader, _masterSh2Bus.PeripheralRegs);
        SaveStateIo.ReadExactly(reader, _slaveSh2Bus.PeripheralRegs);

        Sh2IrqMask[0] = reader.ReadByte();
        Sh2IrqMask[1] = reader.ReadByte();
        Sh2IrqPending[0] = reader.ReadByte();
        Sh2IrqPending[1] = reader.ReadByte();
        _hIntCounterReg = reader.ReadByte();
        _hIntCountdown = reader.ReadInt32();

        for (int i = 0; i < VdpRegs.Length; i++) VdpRegs[i] = reader.ReadUInt16();
        for (int i = 0; i < Palette.Length; i++) Palette[i] = reader.ReadUInt16();
        SaveStateIo.ReadExactly(reader, FrameBuffer[0]);
        SaveStateIo.ReadExactly(reader, FrameBuffer[1]);
        _wasVBlank = reader.ReadBoolean();
        _pendingFrameSelectValue = reader.ReadBoolean();
        _blankFakeCounter = reader.ReadByte();

        for (int i = 0; i < _pwmFifo[0].Length; i++) _pwmFifo[0][i] = reader.ReadUInt16();
        for (int i = 0; i < _pwmFifo[1].Length; i++) _pwmFifo[1][i] = reader.ReadUInt16();
        _pwmFifoCount[0] = reader.ReadInt32();
        _pwmFifoCount[1] = reader.ReadInt32();
        _pwmFifoHead[0] = reader.ReadInt32();
        _pwmFifoHead[1] = reader.ReadInt32();
        _pwmCurrent[0] = reader.ReadInt16();
        _pwmCurrent[1] = reader.ReadInt16();
        _pwmCycleDebt = reader.ReadDouble();
        _pwmIrqCounter = reader.ReadInt32();
    }
}
