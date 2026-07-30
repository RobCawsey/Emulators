using NesSharp.Cpu;

namespace NesSharp.Core;

/// <summary>The NES's CPU-side memory map: 2KB internal RAM mirrored 4x, PPU registers
/// mirrored every 8 bytes, APU/IO registers (currently inert stubs), and everything from
/// $4020 up handed to the cartridge's mapper.</summary>
public sealed class NesBus : IBus
{
    private readonly byte[] _ram = new byte[2048];
    private readonly Ppu2C02 _ppu;
    private readonly IMapper _mapper;
    private Cpu6502? _cpu;
    private Apu2A03? _apu;

    public Controller Controller1 { get; } = new();
    public Controller Controller2 { get; } = new();

    public NesBus(Ppu2C02 ppu, IMapper mapper)
    {
        _ppu = ppu;
        _mapper = mapper;
    }

    /// <summary>NesConsole calls this right after constructing the CPU — NesBus needs a CPU
    /// reference for OAMDMA (to stall it, and to check cycle parity for the 513-vs-514-cycle
    /// rule), but the CPU also needs this bus to exist first, so the reference is late-bound
    /// rather than passed through the constructor.</summary>
    public void AttachCpu(Cpu6502 cpu) => _cpu = cpu;

    /// <summary>Passthrough to the mapper's own IRQ line (e.g. MMC3's scanline counter),
    /// separate from the APU's — NesConsole ORs both into the CPU's shared IRQ input.</summary>
    public bool MapperIrqLine => _mapper.IrqLine;

    /// <summary>Passthrough for mappers with a CPU-cycle-driven IRQ counter (VRC6, VRC7,
    /// Sunsoft FME-7) — NesConsole calls this once per CPU cycle.</summary>
    public void ClockMapperCpu() => _mapper.ClockCpu();

    /// <summary>Passthrough to the mapper's expansion audio output, if any.</summary>
    public float GetMapperAudioSample() => _mapper.GetAudioSample();

    /// <summary>NesConsole calls this once the APU exists, so $4000-$4017's APU-owned
    /// addresses can be routed to it.</summary>
    public void AttachApu(Apu2A03 apu) => _apu = apu;

    public byte Read(ushort address)
    {
        if (address < 0x2000)
        {
            return _ram[address & 0x07FF];
        }
        if (address < 0x4000)
        {
            return _ppu.ReadRegister(address & 0x0007);
        }
        if (address == 0x4015)
        {
            return _apu!.ReadStatus();
        }
        if (address == 0x4016)
        {
            return Controller1.Read();
        }
        if (address == 0x4017)
        {
            return Controller2.Read();
        }
        if (address < 0x4018)
        {
            return 0; // $4000-$4014: write-only APU registers
        }
        return _mapper.CpuRead(address);
    }

    public void Write(ushort address, byte value)
    {
        if (address < 0x2000)
        {
            _ram[address & 0x07FF] = value;
            return;
        }
        if (address < 0x4000)
        {
            _ppu.WriteRegister(address & 0x0007, value);
            return;
        }
        if (address <= 0x4013)
        {
            WriteApuRegister(address, value);
            return;
        }
        if (address == 0x4014)
        {
            RunOamDma(value);
            return;
        }
        if (address == 0x4015)
        {
            _apu!.WriteStatus(value);
            return;
        }
        if (address == 0x4016)
        {
            // The strobe line is wired to both controller ports at once on real hardware.
            Controller1.Write(value);
            Controller2.Write(value);
            return;
        }
        if (address == 0x4017)
        {
            _apu!.WriteFrameCounter(value);
            return;
        }
        _mapper.CpuWrite(address, value);
    }

    private void WriteApuRegister(ushort address, byte value)
    {
        switch (address)
        {
            case 0x4000: _apu!.Pulse1.WriteControl(value); break;
            case 0x4001: _apu!.Pulse1.WriteSweep(value); break;
            case 0x4002: _apu!.Pulse1.WriteTimerLow(value); break;
            case 0x4003: _apu!.Pulse1.WriteTimerHighAndLength(value); break;
            case 0x4004: _apu!.Pulse2.WriteControl(value); break;
            case 0x4005: _apu!.Pulse2.WriteSweep(value); break;
            case 0x4006: _apu!.Pulse2.WriteTimerLow(value); break;
            case 0x4007: _apu!.Pulse2.WriteTimerHighAndLength(value); break;
            case 0x4008: _apu!.Triangle.WriteControl(value); break;
            case 0x400A: _apu!.Triangle.WriteTimerLow(value); break;
            case 0x400B: _apu!.Triangle.WriteTimerHighAndLength(value); break;
            case 0x400C: _apu!.Noise.WriteControl(value); break;
            case 0x400E: _apu!.Noise.WritePeriod(value); break;
            case 0x400F: _apu!.Noise.WriteLength(value); break;
            case 0x4010: _apu!.Dmc.WriteControl(value); break;
            case 0x4011: _apu!.Dmc.WriteDirectLoad(value); break;
            case 0x4012: _apu!.Dmc.WriteSampleAddress(value); break;
            case 0x4013: _apu!.Dmc.WriteSampleLength(value); break;
            // $4009 and $400D are unused.
        }
    }

    /// <summary>Copies the 256-byte page $xx00-$xxFF into OAM (via the same path as OAMDATA
    /// writes, so it inherits OAMADDR wraparound for free) and halts the CPU for the transfer
    /// — 513 cycles normally, 514 if the triggering write landed on an odd CPU cycle.</summary>
    private void RunOamDma(byte page)
    {
        ushort baseAddr = (ushort)(page << 8);
        for (int i = 0; i < 256; i++)
        {
            _ppu.WriteRegister(4, Read((ushort)(baseAddr + i)));
        }
        int stallCycles = 513 + (int)(_cpu!.TotalCycles % 2 != 0 ? 1 : 0);
        _cpu.StallCycles(stallCycles);
    }
}
