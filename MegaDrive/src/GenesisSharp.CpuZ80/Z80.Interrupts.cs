namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    /// <summary>Latched by <see cref="RaiseNonMaskableInterrupt"/>; always serviced at the
    /// next instruction boundary regardless of IFF1.</summary>
    public bool NmiPending { get; private set; }

    /// <summary>Latched by <see cref="RaiseMaskableInterrupt"/>; only serviced while IFF1 is
    /// set. Unlike a real level-held interrupt line, this is a one-shot request consumed
    /// once serviced — there's no device yet that would need to keep asserting it.</summary>
    public bool InterruptPending { get; private set; }

    private byte _interruptDataBusValue;

    /// <summary>NMI — always taken. On the Genesis this line isn't actually used (only the
    /// VDP's maskable INT is wired to the Z80), but it's cheap to support correctly.</summary>
    public void RaiseNonMaskableInterrupt() => NmiPending = true;

    /// <summary>INT — the Genesis wires the VDP's vblank pulse here, which is how the sound
    /// driver knows to advance to the next frame of music. <paramref name="dataBusValue"/> is
    /// what a real interrupting device would place on the data bus: the low byte of a vector
    /// address in IM 2, or an instruction opcode in IM 0 (Genesis code always uses IM 1, which
    /// ignores this value entirely).</summary>
    public void RaiseMaskableInterrupt(byte dataBusValue = 0xFF)
    {
        InterruptPending = true;
        _interruptDataBusValue = dataBusValue;
    }

    /// <summary>Services NMI (unconditionally) or INT (only if IFF1 is set), in that priority
    /// order. Both wake the CPU from HALT. Returns 0 if nothing was serviced.</summary>
    private int ServicePendingInterrupt()
    {
        if (NmiPending)
        {
            NmiPending = false;
            Halted = false;
            Iff2 = Iff1;
            Iff1 = false;
            PushWord(PC);
            PC = 0x0066;
            return 11;
        }

        if (InterruptPending && Iff1)
        {
            InterruptPending = false;
            Halted = false;
            Iff1 = false;
            Iff2 = false;

            switch (InterruptMode)
            {
                case 0:
                    // Genesis code never actually uses IM 0; this approximates the real
                    // behavior (execute whatever instruction the device placed on the bus,
                    // most commonly a single-byte RST) rather than throwing.
                    return 2 + ExecuteMainOpcode(_interruptDataBusValue);
                case 2:
                    PushWord(PC);
                    ushort vectorAddress = (ushort)((I << 8) | _interruptDataBusValue);
                    byte low = _bus.ReadByte(vectorAddress);
                    byte high = _bus.ReadByte((ushort)(vectorAddress + 1));
                    PC = (ushort)((high << 8) | low);
                    return 19;
                default: // IM 1 — the common case for the Genesis's sound driver
                    PushWord(PC);
                    PC = 0x0038;
                    return 13;
            }
        }

        return 0;
    }
}
