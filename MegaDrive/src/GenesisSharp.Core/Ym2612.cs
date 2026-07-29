namespace GenesisSharp.Core;

/// <summary>YM2612 FM synthesis chip — register-level model only. This captures the register
/// write protocol accurately enough that a sound driver's writes succeed and its register
/// state can be inspected: two independently-addressed banks ("Part I" for channels 1-3,
/// "Part II" for channels 4-6), each with its own address-latch/data-write pair of ports.
///
/// There's no LFO, SSG-EG, or channel-3 special mode. But register writes now do drive a
/// genuine 4-operator FM synthesis engine, channel 6's DAC/PCM playback is modeled, and so are
/// Timer A/B (see Ym2612.Timers.cs) — see Ym2612.Synthesis.cs for what does and doesn't match
/// real hardware on the audio side.</summary>
public sealed partial class Ym2612
{
    private readonly byte[] _part1Registers = new byte[256];
    private readonly byte[] _part2Registers = new byte[256];
    private byte _part1Address;
    private byte _part2Address;

    // DAC/PCM sample playback (channel 6 only, Part I registers 0x2A/0x2B): while enabled,
    // this sample-and-hold value replaces channel 6's FM output entirely rather than adding to
    // it -- see Ym2612.Synthesis.cs's GenerateChannelSample. Register 0x2A is a plain
    // single-byte register on real hardware, not backed by any FIFO: each write just overwrites
    // whatever's currently latched, and the DAC outputs whatever that byte is at any given
    // moment. A queue was tried here earlier on the theory that a whole frame's worth of writes
    // would otherwise collapse into just the final byte, "losing" everything but the last
    // sample -- but that's exactly how real hardware behaves too (a write that gets superseded
    // before it's ever sampled was never actually output), and replaying a queued backlog
    // introduced real, audible discontinuities of its own (see DacOverflowCount's history).
    private bool _dacEnabled;
    private byte _dacSample = 0x80; // unsigned 8-bit PCM, 0x80 = the DAC's centered/silent level; always just the most recently written byte

    public void Reset()
    {
        Array.Clear(_part1Registers);
        Array.Clear(_part2Registers);
        _part1Address = 0;
        _part2Address = 0;
        _dacEnabled = false;
        _dacSample = 0x80;
        ClipCount = 0;
        ResetTimers();
        ResetSynthesisState();

        // Unlike every other register, real YM2612 hardware powers on with both pan bits (bit 7
        // = L, bit 6 = R) already set on each channel's 0xB4-0xB6 register, not cleared -- a
        // well-documented quirk several real games depend on (After Burner II among them)
        // instead of setting pan explicitly, and one accurate emulators (BlastEm, Gens) seed
        // this same way at reset rather than leaving it at 0x00. Sonic 1's own sound driver is
        // one of those games: its "SEGA" DAC voice sample never writes channel 6's pan register
        // at all before playing (confirmed against the real driver's disassembly), so without
        // this, GenerateSample's pan-gated mixing was silently muting DAC output on both
        // channels for the entire session -- correctly computed, never actually audible.
        const byte PoweredOnPan = 0xC0;
        _part1Registers[0xB4] = PoweredOnPan;
        _part1Registers[0xB5] = PoweredOnPan;
        _part1Registers[0xB6] = PoweredOnPan;
        _part2Registers[0xB4] = PoweredOnPan;
        _part2Registers[0xB5] = PoweredOnPan;
        _part2Registers[0xB6] = PoweredOnPan;
    }

    public void WriteAddressPart1(byte value) => _part1Address = value;

    public void WriteDataPart1(byte value)
    {
        _part1Registers[_part1Address] = value;
        switch (_part1Address)
        {
            case 0x27:
                HandleTimerControlWrite(value);
                break;
            case 0x28:
                HandleKeyOnOff(value);
                break;
            case 0x2A:
                _dacSample = value;
                break;
            case 0x2B:
                _dacEnabled = (value & 0x80) != 0;
                break;
        }
    }

    public void WriteAddressPart2(byte value) => _part2Address = value;
    public void WriteDataPart2(byte value) => _part2Registers[_part2Address] = value;

    public byte ReadRegisterPart1(int address) => _part1Registers[address & 0xFF];
    public byte ReadRegisterPart2(int address) => _part2Registers[address & 0xFF];

    /// <summary>Bit 7 busy (never modeled, always 0), bits 1-0 Timer B/A overflow -- see
    /// Ym2612.Timers.cs. Real Genesis hardware doesn't wire either timer's overflow to the
    /// Z80's interrupt line (unlike some other OPN2 installations), which is exactly why
    /// drivers poll this register directly rather than waiting for an interrupt.</summary>
    public byte ReadStatus() => (byte)((_timerBOverflow ? 0x02 : 0) | (_timerAOverflow ? 0x01 : 0));
}
