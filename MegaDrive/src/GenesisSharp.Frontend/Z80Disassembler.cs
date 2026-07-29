using System.Text.RegularExpressions;
using GenesisSharp.CpuZ80;

namespace GenesisSharp.Frontend;

/// <summary>Standalone Z80 disassembler for the debug window's "next instruction" readout --
/// entirely separate from <c>Z80</c>'s own decode/execute logic (debug-display-only, never runs
/// during normal emulation). Covers the unprefixed table, CB (bit/rotate), and the common ED and
/// DD/FD (index register) forms. Anything not modeled falls back to a raw hex dump rather than a
/// guess.</summary>
public static class Z80Disassembler
{
    private static readonly string[] Reg8 = { "B", "C", "D", "E", "H", "L", "(HL)", "A" };
    private static readonly string[] Reg16Sp = { "BC", "DE", "HL", "SP" };
    private static readonly string[] Reg16Af = { "BC", "DE", "HL", "AF" };
    private static readonly string[] Condition = { "NZ", "Z", "NC", "C", "PO", "PE", "P", "M" };
    private static readonly string[] AluOp = { "ADD A,", "ADC A,", "SUB ", "SBC A,", "AND ", "XOR ", "OR ", "CP " };
    private static readonly string[] RotOp = { "RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL" };

    /// <summary>Ranges this emulator's own <c>GenesisConsole</c> gives specific meaning to on
    /// the Z80's memory-mapped bus (see its <c>CpuZ80.IBus</c> implementation) -- taken directly
    /// from that decode logic, not general Genesis lore. The YM2612/bank-register entries are
    /// single addresses since the real chip only decodes the low bits of that block (the rest
    /// mirrors); PSG and the 68000 bank window are genuinely address-insensitive ranges in this
    /// emulator's own implementation, not an approximation.</summary>
    private static readonly (ushort Start, ushort End, string Name)[] HardwareRegisters =
    {
        (0x4000, 0x4000, "YM2612_ADDR1"), (0x4001, 0x4001, "YM2612_DATA1"),
        (0x4002, 0x4002, "YM2612_ADDR2"), (0x4003, 0x4003, "YM2612_DATA2"),
        (0x6000, 0x6000, "Z80_BANK_REGISTER"),
        (0x7F00, 0x7FFF, "PSG"),
        (0x8000, 0xFFFF, "68K_BANK_WINDOW"),
    };

    private static readonly Regex HexLiteral = new(@"\$([0-9A-Fa-f]{2,4})", RegexOptions.Compiled);

    public static DisassembledInstruction Decode(ushort pc, IBus bus)
    {
        try
        {
            var reader = new Reader(pc, bus);
            byte op = reader.NextByte();
            string text = AnnotateHardwareRegisters(DecodeOpcode(op, reader, indexReg: null));
            return new DisassembledInstruction(pc, text, reader.Pc - pc, reader.BranchTarget);
        }
        catch
        {
            byte raw = bus.ReadByte(pc);
            return new DisassembledInstruction(pc, $"DB ${raw:X2}", 1, null);
        }
    }

    /// <summary>Decodes <paramref name="count"/> instructions in sequence starting at
    /// <paramref name="startAddress"/> -- the raw building block for a scrollable disassembly
    /// listing; label assignment (<see cref="DisassemblyLabeler"/>) is a separate pass over the
    /// result since it needs the whole run to know which targets land inside it.</summary>
    public static List<DisassembledInstruction> DisassembleRange(ushort startAddress, int count, IBus bus)
    {
        var results = new List<DisassembledInstruction>(count);
        ushort address = startAddress;
        for (int i = 0; i < count; i++)
        {
            var instruction = Decode(address, bus);
            results.Add(instruction);
            address = (ushort)(address + Math.Max(1, instruction.Length));
        }

        return results;
    }

    /// <summary>Appends a trailing comment naming any known hardware address found in the
    /// formatted operand text -- see the 68000 disassembler's equivalent for why this is a
    /// trailing comment rather than an inline substitution.</summary>
    private static string AnnotateHardwareRegisters(string text)
    {
        List<string>? names = null;
        foreach (Match match in HexLiteral.Matches(text))
        {
            ushort value = Convert.ToUInt16(match.Groups[1].Value, 16);
            foreach (var (start, end, name) in HardwareRegisters)
            {
                if (value >= start && value <= end)
                {
                    names ??= new List<string>();
                    if (!names.Contains(name))
                    {
                        names.Add(name);
                    }

                    break;
                }
            }
        }

        return names is null ? text : $"{text}  ; {string.Join(", ", names)}";
    }

    private sealed class Reader
    {
        private readonly IBus _bus;
        public ushort Pc;
        public uint? BranchTarget;
        public Reader(ushort pc, IBus bus) { Pc = pc; _bus = bus; }
        public byte NextByte() => _bus.ReadByte(Pc++);
        public sbyte NextSByte() => (sbyte)NextByte();
        public ushort NextWord() { ushort lo = NextByte(); ushort hi = NextByte(); return (ushort)(lo | (hi << 8)); }
    }

    private static string DecodeOpcode(byte op, Reader r, string? indexReg)
    {
        if (op == 0xCB) return DecodeCb(r, indexReg);
        if (op == 0xED) return DecodeEd(r);
        if (op == 0xDD) return DecodeOpcode(r.NextByte(), r, "IX");
        if (op == 0xFD) return DecodeOpcode(r.NextByte(), r, "IY");

        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;

        if (x == 0)
        {
            switch (z)
            {
                case 0:
                    if (y == 0) return "NOP";
                    if (y == 1) return "EX AF,AF'";
                    if (y == 2)
                    {
                        sbyte d = r.NextSByte();
                        r.BranchTarget = (ushort)(r.Pc + d);
                        return $"DJNZ *{Signed(d)}";
                    }
                    if (y == 3)
                    {
                        sbyte d = r.NextSByte();
                        r.BranchTarget = (ushort)(r.Pc + d);
                        return $"JR *{Signed(d)}";
                    }
                    {
                        sbyte d2 = r.NextSByte();
                        r.BranchTarget = (ushort)(r.Pc + d2);
                        return $"JR {Condition[y - 4]},*{Signed(d2)}";
                    }
                case 1:
                    if ((y & 1) == 0)
                    {
                        ushort imm = r.NextWord();
                        return $"LD {Reg16Sp[y >> 1]},${imm:X4}";
                    }
                    return $"ADD {IndexOrHl(indexReg)},{Reg16Sp[y >> 1]}";
                case 2:
                    return y switch
                    {
                        0 => "LD (BC),A",
                        1 => "LD A,(BC)",
                        2 => "LD (DE),A",
                        3 => "LD A,(DE)",
                        4 => $"LD (${r.NextWord():X4}),{IndexOrHl(indexReg)}",
                        5 => $"LD {IndexOrHl(indexReg)},(${r.NextWord():X4})",
                        6 => $"LD (${r.NextWord():X4}),A",
                        _ => $"LD A,(${r.NextWord():X4})",
                    };
                case 3:
                    return (y & 1) == 0 ? $"INC {Reg16Sp[y >> 1]}" : $"DEC {Reg16Sp[y >> 1]}";
                case 4:
                    return $"INC {Operand8(y, indexReg, r)}";
                case 5:
                    return $"DEC {Operand8(y, indexReg, r)}";
                case 6:
                    { byte imm = r.NextByte(); return $"LD {Operand8(y, indexReg, r)},${imm:X2}"; }
                case 7:
                    return y switch
                    {
                        0 => "RLCA", 1 => "RRCA", 2 => "RLA", 3 => "RRA",
                        4 => "CPL", 5 => "CPL", 6 => "SCF", _ => "CCF",
                    };
            }
        }
        else if (x == 1)
        {
            if (y == 6 && z == 6) return "HALT";
            return $"LD {Operand8(y, indexReg, r)},{Operand8(z, indexReg, r)}";
        }
        else if (x == 2)
        {
            return $"{AluOp[y]}{Operand8(z, indexReg, r)}";
        }
        else
        {
            switch (z)
            {
                case 0: return $"RET {Condition[y]}";
                case 1:
                    if ((y & 1) == 0) return $"POP {Reg16Af[y >> 1]}";
                    return y switch
                    {
                        1 => "RET",
                        3 => "EXX",
                        5 => $"JP {IndexOrHl(indexReg)}",
                        _ => $"LD SP,{IndexOrHl(indexReg)}",
                    };
                case 2:
                    {
                        ushort target = r.NextWord();
                        r.BranchTarget = target;
                        return $"JP {Condition[y]},${target:X4}";
                    }
                case 3:
                    return y switch
                    {
                        0 => DecodeJpUnconditional(r),
                        1 => DecodeCb(r, null),
                        2 => $"OUT (${r.NextByte():X2}),A",
                        3 => $"IN A,(${r.NextByte():X2})",
                        4 => $"EX (SP),{IndexOrHl(indexReg)}",
                        5 => "EX DE,HL",
                        6 => "DI",
                        _ => "EI",
                    };
                case 4:
                    {
                        ushort target = r.NextWord();
                        r.BranchTarget = target;
                        return $"CALL {Condition[y]},${target:X4}";
                    }
                case 5:
                    if ((y & 1) == 0) return $"PUSH {Reg16Af[y >> 1]}";
                    return y switch { 1 => DecodeCallUnconditional(r), 3 => "DD prefix", 5 => "ED prefix", _ => "FD prefix" };
                case 6:
                    { byte imm = r.NextByte(); return $"{AluOp[y]}${imm:X2}"; }
                case 7:
                    r.BranchTarget = (ushort)(y * 8);
                    return $"RST ${y * 8:X2}";
            }
        }

        return $"DB ${op:X2}";
    }

    private static string DecodeJpUnconditional(Reader r)
    {
        ushort target = r.NextWord();
        r.BranchTarget = target;
        return $"JP ${target:X4}";
    }

    private static string DecodeCallUnconditional(Reader r)
    {
        ushort target = r.NextWord();
        r.BranchTarget = target;
        return $"CALL ${target:X4}";
    }

    private static string IndexOrHl(string? indexReg) => indexReg ?? "HL";

    private static string Operand8(int index, string? indexReg, Reader r)
    {
        if (indexReg is null || index != 6)
        {
            return Reg8[index];
        }

        sbyte disp = r.NextSByte();
        return $"({indexReg}{Signed(disp)})";
    }

    private static string Signed(int displacement) => displacement >= 0 ? $"+{displacement}" : displacement.ToString();

    private static string DecodeCb(Reader r, string? indexReg)
    {
        sbyte disp = 0;
        if (indexReg is not null)
        {
            disp = r.NextSByte();
        }

        byte op = r.NextByte();
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;
        string operand = indexReg is not null ? $"({indexReg}{Signed(disp)})" : Reg8[z];

        return x switch
        {
            0 => $"{RotOp[y]} {operand}",
            1 => $"BIT {y},{operand}",
            2 => $"RES {y},{operand}",
            _ => $"SET {y},{operand}",
        };
    }

    private static string DecodeEd(Reader r)
    {
        byte op = r.NextByte();
        int x = op >> 6, y = (op >> 3) & 7, z = op & 7;

        if (x == 1)
        {
            switch (z)
            {
                case 0: return y == 6 ? "IN (C)" : $"IN {Reg8[y]},(C)";
                case 1: return y == 6 ? "OUT (C),0" : $"OUT (C),{Reg8[y]}";
                case 2: return (y & 1) == 0 ? $"SBC HL,{Reg16Sp[y >> 1]}" : $"ADC HL,{Reg16Sp[y >> 1]}";
                case 3:
                    {
                        ushort addr = r.NextWord();
                        return (y & 1) == 0 ? $"LD (${addr:X4}),{Reg16Sp[y >> 1]}" : $"LD {Reg16Sp[y >> 1]},(${addr:X4})";
                    }
                case 4: return "NEG";
                case 5: return y == 1 ? "RETI" : "RETN";
                case 6: return $"IM {y switch { 2 or 6 => 1, 3 or 7 => 2, _ => 0 }}";
                case 7:
                    return y switch
                    {
                        0 => "LD I,A", 1 => "LD R,A", 2 => "LD A,I", 3 => "LD A,R",
                        4 => "RRD", _ => "RLD",
                    };
            }
        }
        else if (x == 2 && z <= 3 && y >= 4)
        {
            string[][] rows =
            {
                new[] { "LDI", "CPI", "INI", "OUTI" },
                new[] { "LDD", "CPD", "IND", "OUTD" },
                new[] { "LDIR", "CPIR", "INIR", "OTIR" },
                new[] { "LDDR", "CPDR", "INDR", "OTDR" },
            };
            return rows[y - 4][z];
        }

        return $"DB $ED,${op:X2}";
    }
}
