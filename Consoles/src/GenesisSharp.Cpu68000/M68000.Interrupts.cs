namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Requests an interrupt at the given priority level (1-7, matching the IPL2-0
    /// lines — level 7 is non-maskable). If a higher level is already pending and hasn't been
    /// serviced yet, the lower request is dropped, mirroring how the external priority
    /// encoder that feeds a real 68000's IPL lines would present only the highest currently
    /// asserted level. There's no way to model "the source lowered its request before the CPU
    /// got to it" yet (no device holds a level long enough for that to matter without a VDP),
    /// so once accepted here a request stays pending until serviced.</summary>
    public void RaiseInterrupt(int level)
    {
        if (level < 1 || level > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Interrupt level must be 1-7.");
        }

        if (level > PendingInterruptLevel)
        {
            PendingInterruptLevel = level;
        }
    }

    /// <summary>Checks the pending level against SR's interrupt priority mask (bits 10-8) and,
    /// if it qualifies (higher than the mask, or level 7 which is always taken), runs the
    /// standard exception-entry sequence with the mask updated to the new level. Returns 0 if
    /// nothing was serviced. Autovectored — vector = 24 + level — which is how the real
    /// Genesis wires its VDP interrupt (level 4, vector 28, address 0x70).</summary>
    private int ServicePendingInterrupt()
    {
        if (PendingInterruptLevel == 0)
        {
            return 0;
        }

        int currentMask = (SR >> 8) & 7;
        if (PendingInterruptLevel <= currentMask && PendingInterruptLevel != 7)
        {
            return 0;
        }

        int level = PendingInterruptLevel;
        PendingInterruptLevel = 0;

        ushort savedSr = SR;
        PushLong(PC);
        PushWord(savedSr);
        SR = (ushort)((SR & ~0x0700) | (level << 8) | SupervisorBit);
        PC = _bus.ReadLong((uint)((24 + level) * 4));

        return 44;
    }
}
