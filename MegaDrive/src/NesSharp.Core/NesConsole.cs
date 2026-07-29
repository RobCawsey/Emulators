using NesSharp.Cpu;

namespace NesSharp.Core;

/// <summary>Ties the CPU, PPU, and cartridge together and drives the master clock: one
/// CPU cycle per <see cref="Clock"/> call, with the PPU ticked 3x alongside it (the NES's
/// fixed CPU:PPU clock ratio) so the two always advance in lockstep.</summary>
public sealed class NesConsole
{
    public Cpu6502 Cpu { get; }
    public Ppu2C02 Ppu { get; }
    public Apu2A03 Apu { get; }
    public NesBus Bus { get; }

    public NesConsole(Cartridge cartridge)
    {
        IMapper mapper = CreateMapper(cartridge);
        Ppu = new Ppu2C02(mapper);
        Bus = new NesBus(Ppu, mapper);
        Apu = new Apu2A03(Bus);
        Bus.AttachApu(Apu);
        Cpu = new Cpu6502(Bus);
        Bus.AttachCpu(Cpu);
        Apu.AttachCpu(Cpu);
    }

    private static IMapper CreateMapper(Cartridge cartridge) => cartridge.MapperNumber switch
    {
        0 => new Mapper0(cartridge),
        1 => new Mapper1(cartridge),
        2 => new Mapper2(cartridge),
        3 => new Mapper3(cartridge),
        4 => new Mapper4(cartridge),
        7 => new Mapper7(cartridge),
        9 => new Mapper9(cartridge),
        10 => new Mapper10(cartridge),
        19 => new Mapper19(cartridge),
        24 => new Mapper24(cartridge), // VRC6a
        26 => new Mapper24(cartridge), // VRC6b — see Mapper24's doc comment on the address-line-swap simplification
        66 => new Mapper66(cartridge),
        69 => new Mapper69(cartridge),
        85 => new Mapper85(cartridge),
        _ => throw new NotSupportedException($"Mapper {cartridge.MapperNumber} is not implemented yet."),
    };

    public void Clock()
    {
        Cpu.Clock();
        for (int i = 0; i < 3; i++)
        {
            Ppu.Tick();
            if (Ppu.TakeNmiRequest())
            {
                Cpu.RaiseNmi();
            }
        }
        Bus.ClockMapperCpu();
        Apu.Clock(Bus.GetMapperAudioSample());
        Cpu.IrqLine = Apu.IrqLine || Bus.MapperIrqLine;
    }

    /// <summary>Runs the CPU's 7-cycle reset sequence through the console's own clock, so
    /// the PPU advances alongside it exactly as it would on real hardware.</summary>
    public void Reset()
    {
        Cpu.Reset();
        do
        {
            Clock();
        } while (Cpu.IsMidInstruction);
    }

    /// <summary>Runs exactly one full CPU instruction (and whatever PPU ticks accompany it).</summary>
    public void StepInstruction()
    {
        do
        {
            Clock();
        } while (Cpu.IsMidInstruction);
    }
}
