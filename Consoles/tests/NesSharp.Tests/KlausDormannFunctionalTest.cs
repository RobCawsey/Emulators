using NesSharp.Cpu;
using Xunit;

namespace NesSharp.Tests;

/// <summary>
/// Runs Klaus Dormann's 6502 functional test ROM — an exhaustive, self-checking exerciser
/// of every official opcode/addressing-mode combination, independent of any NES-specific
/// behavior. See TestRoms/README.md for provenance and license.
/// </summary>
public class KlausDormannFunctionalTest
{
    private const string RomPath = "TestRoms/6502_functional_test.bin";

    /// <summary>
    /// The exact PC where this run is expected to stop, verified against the published
    /// listing for this exact binary (bin_files/6502_functional_test.lst in the source repo).
    /// This binary is built with decimal-mode tests enabled (disable_decimal = 0). The very
    /// first decimal ADC ($99 + $99 + carry) is checked at $3475 ("cmp adrl") and traps to a
    /// self-branch at $3477 ("bne *") if the result doesn't match the expected BCD-corrected
    /// value — which it never will here, since the NES's Ricoh 2A03 (and this core, matching
    /// it) never implements decimal-mode correction. Everything from the test's start ($0400)
    /// up to this address exhaustively exercises every official opcode in binary mode, so
    /// reaching exactly this trap — and no earlier one — is this test's definition of success.
    /// </summary>
    private const ushort ExpectedStopAddress = 0x3477;

    // Test #41 alone ("full binary add/subtract", exhaustive over all 65536 operand/carry
    // combinations) burns tens of millions of cycles before the decimal-mode test (#42) even
    // starts, so the budget needs to comfortably clear that, not just the handful of cycles
    // the actual stop condition needs.
    private const long CycleBudget = 500_000_000;

    [Fact]
    public void RunsCleanUntilTheExpectedDecimalModeStop()
    {
        byte[] rom = File.ReadAllBytes(RomPath);
        Assert.Equal(65536, rom.Length);

        var bus = new FlatRamBus();
        bus.Load(0x0000, rom);

        var cpu = new Cpu6502(bus)
        {
            PC = 0x0400,
            SP = 0xFD,
        };

        ushort pc = cpu.PC;
        while (cpu.TotalCycles < CycleBudget)
        {
            ushort pcBeforeInstruction = cpu.PC;
            cpu.RunUntilBoundary();
            pc = cpu.PC;
            if (pc == pcBeforeInstruction)
            {
                break; // self-branch trap: either the suite's success loop or a failed check
            }
        }

        Assert.True(cpu.TotalCycles < CycleBudget,
            $"Never trapped after {CycleBudget} cycles — PC last seen at 0x{pc:X4}, " +
            $"test_case=0x{bus.Peek(0x0200):X2}.");
        Assert.Equal(ExpectedStopAddress, pc);
    }
}
