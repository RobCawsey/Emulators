namespace GenesisSharp.CpuZ80;

public sealed partial class Z80
{
    // ALU op-index order (bits 5-3 of the 0x80-0xBF grid and the 0xC6-based immediate forms):
    // 0=ADD, 1=ADC, 2=SUB, 3=SBC, 4=AND, 5=XOR, 6=OR, 7=CP.

    private int ExecuteAluRegisterOp(byte opcode)
    {
        int op = (opcode >> 3) & 7, src = opcode & 7;
        ApplyAluOp(op, ReadR8(src));
        return src == 6 ? 7 : 4;
    }

    private int ExecuteAluImmediate(int op)
    {
        ApplyAluOp(op, FetchByte());
        return 7;
    }

    private void ApplyAluOp(int op, byte value)
    {
        byte result;
        switch (op)
        {
            case 0:
                SetFlagsAdd8(A, value, 0, out result);
                A = result;
                break;
            case 1:
                SetFlagsAdd8(A, value, FlagCarry ? 1 : 0, out result);
                A = result;
                break;
            case 2:
                SetFlagsSub8(A, value, 0, out result);
                A = result;
                break;
            case 3:
                SetFlagsSub8(A, value, FlagCarry ? 1 : 0, out result);
                A = result;
                break;
            case 4:
                A = (byte)(A & value);
                SetFlagsLogic(A);
                FlagHalfCarry = true; // AND always sets H, unlike OR/XOR
                break;
            case 5:
                A = (byte)(A ^ value);
                SetFlagsLogic(A);
                break;
            case 6:
                A = (byte)(A | value);
                SetFlagsLogic(A);
                break;
            case 7:
                SetFlagsSub8(A, value, 0, out _); // CP: flags only, A unchanged
                break;
        }
    }
}
