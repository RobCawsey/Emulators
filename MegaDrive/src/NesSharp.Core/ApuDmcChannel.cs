using NesSharp.Cpu;

namespace NesSharp.Core;

/// <summary>
/// Delta modulation channel: plays back a 1-bit-per-sample delta-encoded stream fetched via
/// DMA from CPU memory, driving a 7-bit output level up or down by 2 per bit. The DMA fetch
/// stalls the CPU — real hardware's stall varies (4 cycles typically, sometimes +1/+2
/// depending on CPU cycle alignment, and interacts with any simultaneous OAMDMA); this
/// always stalls exactly 4 cycles, a documented simplification rather than an attempt at
/// that level of precision.
/// </summary>
public sealed class ApuDmcChannel
{
    private static readonly ushort[] RateTable =
    {
        428, 380, 340, 320, 286, 254, 226, 214, 190, 160, 142, 128, 106, 84, 72, 54,
    };

    private readonly IBus _bus;
    private Cpu6502? _cpu;

    private bool _irqEnable;
    private bool _loop;
    private ushort _timerPeriod = RateTable[0];
    private ushort _timerCounter;

    private ushort _sampleAddress = 0xC000;
    private int _sampleLengthBytes = 1;
    private ushort _currentAddress;
    private int _bytesRemaining;

    private byte? _sampleBuffer;
    private byte _shiftRegister;
    private int _bitsRemaining;
    private bool _silenceFlag = true;
    private byte _outputLevel;

    public ApuDmcChannel(IBus bus)
    {
        _bus = bus;
    }

    /// <summary>NesConsole attaches this once the CPU exists — see NesBus.AttachCpu for why
    /// this can't just be a constructor parameter.</summary>
    public void AttachCpu(Cpu6502 cpu) => _cpu = cpu;

    public bool IrqFlag { get; private set; }
    public int BytesRemaining => _bytesRemaining;
    public byte Output => _outputLevel;

    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            _bytesRemaining = 0;
        }
        else if (_bytesRemaining == 0)
        {
            Restart();
        }
    }

    private void Restart()
    {
        _currentAddress = _sampleAddress;
        _bytesRemaining = _sampleLengthBytes;
    }

    /// <summary>$4010 — IRQ enable, loop, rate index.</summary>
    public void WriteControl(byte value)
    {
        _irqEnable = (value & 0x80) != 0;
        _loop = (value & 0x40) != 0;
        _timerPeriod = RateTable[value & 0x0F];
        if (!_irqEnable)
        {
            IrqFlag = false;
        }
    }

    /// <summary>$4011 — direct 7-bit output level load, for software-driven playback tricks.</summary>
    public void WriteDirectLoad(byte value) => _outputLevel = (byte)(value & 0x7F);

    /// <summary>$4012 — sample address = $C000 + value*64.</summary>
    public void WriteSampleAddress(byte value) => _sampleAddress = (ushort)(0xC000 + value * 64);

    /// <summary>$4013 — sample length in bytes = value*16 + 1.</summary>
    public void WriteSampleLength(byte value) => _sampleLengthBytes = value * 16 + 1;

    public void ClockTimer()
    {
        if (_sampleBuffer is null && _bytesRemaining > 0)
        {
            FetchByte();
        }

        if (_timerCounter == 0)
        {
            _timerCounter = _timerPeriod;
            ClockOutputUnit();
        }
        else
        {
            _timerCounter--;
        }
    }

    private void FetchByte()
    {
        _sampleBuffer = _bus.Read(_currentAddress);
        _currentAddress = _currentAddress == 0xFFFF ? (ushort)0x8000 : (ushort)(_currentAddress + 1);
        _bytesRemaining--;
        if (_bytesRemaining == 0)
        {
            if (_loop)
            {
                Restart();
            }
            else if (_irqEnable)
            {
                IrqFlag = true;
            }
        }
        _cpu?.StallCycles(4);
    }

    private void ClockOutputUnit()
    {
        if (_bitsRemaining == 0)
        {
            _bitsRemaining = 8;
            if (_sampleBuffer.HasValue)
            {
                _silenceFlag = false;
                _shiftRegister = _sampleBuffer.Value;
                _sampleBuffer = null;
            }
            else
            {
                _silenceFlag = true;
            }
        }

        if (!_silenceFlag)
        {
            if ((_shiftRegister & 0x01) != 0)
            {
                if (_outputLevel <= 125)
                {
                    _outputLevel += 2;
                }
            }
            else
            {
                if (_outputLevel >= 2)
                {
                    _outputLevel -= 2;
                }
            }
        }
        _shiftRegister >>= 1;
        _bitsRemaining--;
    }
}
