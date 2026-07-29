namespace GenesisSharp.Core;

public sealed partial class Vdp
{
    private void RunDma()
    {
        switch (CurrentDmaMode)
        {
            case DmaMode.MemoryToVdp: RunMemoryToVdpDma(); break;
            case DmaMode.VramCopy: RunVramCopyDma(); break;
        }
    }

    /// <summary>VRAM fill — triggered by the control-word DMA setup, but the actual fill runs
    /// on the *next data-port write*, which supplies the fill byte (its low 8 bits). Known
    /// simplification: real hardware's first write lands at (address XOR 1) due to how the
    /// fill logic reuses the data port's byte lane; this just fills sequentially from address.</summary>
    private void RunVramFill(ushort dataPortValue)
    {
        byte fillByte = (byte)dataPortValue;
        int length = DmaLength == 0 ? 0x10000 : DmaLength;
        uint address = _address;

        for (int i = 0; i < length; i++)
        {
            Vram[address & (VramSize - 1)] = fillByte;
            VramWritten?.Invoke(address, fillByte);
            address = (address + AutoIncrement) & 0x1FFFF;
        }

        _address = address;
        ChargeDmaStall(length, slotsPerUnit: 1);
    }

    /// <summary>68000 memory (ROM/RAM) to VRAM/CRAM/VSRAM, via <see cref="ExternalMemoryRead"/>.
    /// Transfers whole words, matching the data port's own write granularity.</summary>
    private void RunMemoryToVdpDma()
    {
        if (ExternalMemoryRead is null)
        {
            return;
        }

        int length = DmaLength == 0 ? 0x10000 : DmaLength;
        uint source = DmaSourceAddress;
        uint destination = _address;
        VdpTarget target = TargetFromWriteCode(_code);

        for (int i = 0; i < length; i++)
        {
            byte high = ExternalMemoryRead(source);
            byte low = ExternalMemoryRead(source + 1);
            WriteTarget(target, destination, (ushort)((high << 8) | low));
            source += 2;
            destination = (destination + AutoIncrement) & 0x1FFFF;
        }

        _address = destination;
        ChargeDmaStall(length, SlotsForTarget(target));
    }

    /// <summary>VRAM to VRAM, byte-oriented. Source/length registers are read the same way as
    /// the memory-copy mode (same underlying registers), but interpreted directly as a VRAM
    /// byte address here rather than a 68000 memory address.</summary>
    private void RunVramCopyDma()
    {
        int length = DmaLength == 0 ? 0x10000 : DmaLength;
        uint source = DmaSourceAddress;
        uint destination = _address;

        for (int i = 0; i < length; i++)
        {
            Vram[destination & (VramSize - 1)] = Vram[source & (VramSize - 1)];
            VramWritten?.Invoke(destination, Vram[destination & (VramSize - 1)]);
            source++;
            destination = (destination + AutoIncrement) & 0x1FFFF;
        }

        _address = destination;
        // One read + one write per byte -- confirmed against genesis-plus-gx's vdp_ctrl.c
        // comments ("DMA Copy: one read + one write = 2 access"), which halves its documented
        // VRAM-copy throughput relative to a plain fill (8/83/9/102 access-slots-per-line vs
        // fill's 16/166/18/204) for exactly this reason. Previously charged the same
        // slotsPerUnit as a fill (1), undercharging copy DMAs by 2x.
        ChargeDmaStall(length, slotsPerUnit: 2);
    }
}
