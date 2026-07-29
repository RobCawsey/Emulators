namespace GenesisSharp.Core;

/// <summary>One of the console's I/O ports (a general-purpose bidirectional 7-bit register
/// pair, same chip design backing all three physical ports — two controller ports plus the
/// rarely-used EXT port). <see cref="Direction"/> is the control register: bit N set means
/// data-line N is currently configured as an output the 68000 drives, clear means it's an
/// input the attached device (here, <see cref="Pad"/>) drives.
///
/// Line 6 is TH, which real software always configures as an output to drive <see
/// cref="Pad"/>'s multiplexing protocol — reading it back just echoes whatever was last
/// written. Line 7 is unused and always reads high. The serial I/O registers this same chip
/// also exposes (Tx/Rx/S-ctrl, for the EXT port's RS-232-ish mode) aren't modeled at all —
/// Genesis software essentially never uses them.
///
/// This port also tracks TH transitions for <see cref="GamePad.SixButton"/>'s detection
/// sequence: on real hardware, a 6-button pad's internal counter advances one step each time
/// TH flips, but auto-resets if too long passes between flips — a small hardware timer that's
/// what lets a plain, slowly-polling 3-button game share the exact same wiring without ever
/// stumbling into the extended steps by accident. That timeout duration
/// (<see cref="SequenceTimeoutCycles"/>) is the one part of this whole feature I don't have
/// strong recall of — the step sequence itself is widely and consistently documented, but the
/// exact reset delay is recalled with only moderate confidence.</summary>
public sealed class ControllerPort
{
    /// <summary>~1.5ms at NTSC's ~7.67MHz 68000 clock — long enough that a game's normal
    /// once-per-frame D-pad poll (which alternates TH roughly every 16ms) never advances the
    /// sequence past step 1, but short enough that a deliberate rapid-fire read routine (every
    /// transition within a handful of instructions) sails through all six steps.</summary>
    private const long SequenceTimeoutCycles = 11500;

    public GamePad Pad { get; } = new();

    private byte _outputLatch;
    private bool? _lastTh;
    private int _sequenceStep;
    private long _lastTransitionCycle;

    public byte Direction { get; set; }

    public void Reset()
    {
        _outputLatch = 0;
        Direction = 0;
        _lastTh = null;
        _sequenceStep = 0;
        _lastTransitionCycle = 0;
    }

    public byte ReadData()
    {
        bool th = (_outputLatch & 0x40) != 0;
        byte inputBits = (byte)(Pad.ReadDataLines(th, _sequenceStep) | 0x80); // bit 7: unused, reads high
        return (byte)((_outputLatch & Direction) | (inputBits & ~Direction));
    }

    /// <summary><paramref name="currentCycle"/> is the 68000's running cycle count (<see
    /// cref="Cpu68000.M68000.TotalCycles"/>), used only to time out the 6-button sequence
    /// described in the type remarks — defaults to 0 for callers (tests, mostly) that don't
    /// care about that timing, which is harmless as long as <see cref="GamePad.SixButton"/>
    /// is left false.</summary>
    public void WriteData(byte value, long currentCycle = 0)
    {
        bool th = (value & 0x40) != 0;

        if (_lastTh is null)
        {
            // First write ever (or since Reset): nothing to compare against yet, just
            // establishes the starting step at whichever parity matches this TH level.
            _sequenceStep = th ? 0 : 1;
        }
        else if (th != _lastTh)
        {
            _sequenceStep = currentCycle - _lastTransitionCycle > SequenceTimeoutCycles
                ? (th ? 0 : 1) // too slow -- restart the sequence at this transition
                : (_sequenceStep + 1) % 6;
            _lastTransitionCycle = currentCycle;
        }

        _lastTh = th;
        _outputLatch = value;
    }

    public void SaveState(BinaryWriter writer)
    {
        writer.Write(_outputLatch);
        writer.Write(_lastTh.HasValue);
        if (_lastTh.HasValue) writer.Write(_lastTh.Value);
        writer.Write(_sequenceStep);
        writer.Write(_lastTransitionCycle);
        writer.Write(Direction);
        Pad.SaveState(writer);
    }

    public void LoadState(BinaryReader reader)
    {
        _outputLatch = reader.ReadByte();
        _lastTh = reader.ReadBoolean() ? reader.ReadBoolean() : null;
        _sequenceStep = reader.ReadInt32();
        _lastTransitionCycle = reader.ReadInt64();
        Direction = reader.ReadByte();
        Pad.LoadState(reader);
    }
}
