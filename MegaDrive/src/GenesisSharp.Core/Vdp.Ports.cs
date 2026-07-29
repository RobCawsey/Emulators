namespace GenesisSharp.Core;

public enum VdpTarget { Vram, Cram, Vsram }

public sealed partial class Vdp
{
    private ushort? _pendingCommandWord;
    private uint _address;
    private int _code;
    private bool _dmaFillPending;

    public event Action<uint, ushort>? CramWritten;
    public event Action<uint, ushort>? VramWritten;

    /// <summary>Fires whenever a DMA operation is about to run — mode, the raw length register
    /// value (0 means 65536, per <see cref="Vdp.Dma"/>'s length-0 handling), source address, and
    /// the current destination address. Debugging hook only.</summary>
    public event Action<DmaMode, ushort, uint, uint>? DmaTriggered;

    public event Action<int, byte>? RegisterWritten;

    private void ResetPortState()
    {
        _pendingCommandWord = null;
        _address = 0;
        _code = 0;
        _dmaFillPending = false;
        ResetFifoState();
    }

    /// <summary>The first control-port word this sequence would be latching before a second
    /// word arrives — a register write (top 2 bits "10") short-circuits that and completes
    /// immediately.</summary>
    public void WriteControlPort(ushort value)
    {
        if (_pendingCommandWord.HasValue)
        {
            ushort first = _pendingCommandWord.Value;
            _pendingCommandWord = null;

            // Second control word layout: bits 7-4 are CD5-CD2, bits 1-0 are address bits
            // A15-A14 (bits 3-2 unused). Confirmed against the well-known VSRAM_ADDR_CMD
            // constant ($40000010): its low word is 0x0010 (bit 4 set), and VSRAM write's code
            // (0b000101) has only CD2 set among CD2-CD5 -- so CD2 must be bit 4. Found via
            // real-ROM testing (Omega Blast) after this read bits 5-2 as CD5-CD2, which was two
            // bits too low and made the DMA-trigger check below look at the wrong bit entirely --
            // a control-word pair whose real CD5 (bit 7) was set to request DMA went undetected
            // whenever the wrongly-read bit 5 happened to be clear.
            _address = (uint)((first & 0x3FFF) | ((value & 0x0003) << 14));
            _code = ((first >> 14) & 0x03) | (((value >> 4) & 0x0F) << 2);

            if ((_code & 0x20) != 0 && DmaEnabled)
            {
                if (CurrentDmaMode == DmaMode.VramFill)
                {
                    _dmaFillPending = true;
                }
                else
                {
                    DmaTriggered?.Invoke(CurrentDmaMode, DmaLength, DmaSourceAddress, _address);
                    RunDma();
                }
            }

            return;
        }

        if ((value & 0xC000) == 0x8000)
        {
            int register = (value >> 8) & 0x1F;
            if (register < Registers.Length)
            {
                Registers[register] = (byte)value;
                RegisterWritten?.Invoke(register, (byte)value);
            }

            return;
        }

        _pendingCommandWord = value;
    }

    public void WriteDataPort(ushort value)
    {
        _pendingCommandWord = null;

        if (_dmaFillPending)
        {
            _dmaFillPending = false;
            DmaTriggered?.Invoke(DmaMode.VramFill, DmaLength, DmaSourceAddress, _address);
            RunVramFill(value);
            return;
        }

        VdpTarget target = TargetFromWriteCode(_code);
        EnqueueFifoEntry(target);
        WriteTarget(target, _address, value);
        AdvanceAddress();
    }

    public ushort ReadDataPort()
    {
        _pendingCommandWord = null;
        ushort value = ReadTarget(TargetFromReadCode(_code), _address);
        AdvanceAddress();
        return value;
    }

    private void AdvanceAddress() => _address = (_address + AutoIncrement) & 0x1FFFF;

    private void WriteTarget(VdpTarget target, uint address, ushort value)
    {
        switch (target)
        {
            case VdpTarget.Vram:
                Vram[address & (VramSize - 1)] = (byte)(value >> 8);
                Vram[(address + 1) & (VramSize - 1)] = (byte)value;
                VramWritten?.Invoke(address, value);
                break;
            case VdpTarget.Cram:
                Cram[(address >> 1) % CramColorCount] = value;
                CramWritten?.Invoke(address, value);
                break;
            case VdpTarget.Vsram:
                Vsram[(address >> 1) % VsramSize] = value;
                break;
        }
    }

    private ushort ReadTarget(VdpTarget target, uint address) => target switch
    {
        VdpTarget.Vram => (ushort)((Vram[address & (VramSize - 1)] << 8) | Vram[(address + 1) & (VramSize - 1)]),
        VdpTarget.Cram => Cram[(address >> 1) % CramColorCount],
        VdpTarget.Vsram => Vsram[(address >> 1) % VsramSize],
        _ => 0,
    };

    // Code values below are the commonly-cited canonical ones (VRAM=0/1, CRAM=8/3,
    // VSRAM=4/5, +0x20 for the DMA variants of the write codes) — same confidence caveat as
    // the rest of the register/port layout in this file.
    private static VdpTarget TargetFromWriteCode(int code) => (code & 0x1F) switch
    {
        0x03 => VdpTarget.Cram,
        0x05 => VdpTarget.Vsram,
        _ => VdpTarget.Vram,
    };

    private static VdpTarget TargetFromReadCode(int code) => code switch
    {
        0x08 => VdpTarget.Cram,
        0x04 => VdpTarget.Vsram,
        _ => VdpTarget.Vram,
    };
}
