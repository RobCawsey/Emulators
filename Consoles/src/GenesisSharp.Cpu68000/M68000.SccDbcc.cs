namespace GenesisSharp.Cpu68000;

public sealed partial class M68000
{
    /// <summary>Scc — sets a byte-sized destination to all-1s if the condition holds, all-0s
    /// otherwise. Register destinations are cheaper than memory ones, and (per the real chip)
    /// a true result costs 2 cycles more than a false one when the destination is a Dn.</summary>
    private int ExecuteScc(ushort opcode, Condition condition, int eaMode, int eaReg)
    {
        var (ea, cycles) = DecodeEa(eaMode, eaReg, Size.Byte);
        bool result = TestCondition(condition);
        WriteEa(ea, Size.Byte, result ? 0xFFu : 0x00u);

        if (ea.Kind == EaKind.DataRegister)
        {
            return result ? 6 : 4;
        }

        return 8 + cycles;
    }

    /// <summary>DBcc — the classic "decrement and branch until -1, or until cc" loop
    /// instruction. If the condition is already true, the loop ends immediately without
    /// touching the counter. Otherwise Dn's low word is decremented; if it didn't wrap past
    /// -1, branch back, else fall through.</summary>
    private int ExecuteDbcc(Condition condition, int reg)
    {
        uint baseAddress = PC;
        uint displacement = FetchDisplacement16();

        if (TestCondition(condition))
        {
            return 12;
        }

        ushort counter = (ushort)((D[reg] & 0xFFFF) - 1);
        D[reg] = (D[reg] & 0xFFFF_0000) | counter;

        if (counter != 0xFFFF)
        {
            PC = baseAddress + displacement;
            return 10;
        }

        return 14;
    }
}
