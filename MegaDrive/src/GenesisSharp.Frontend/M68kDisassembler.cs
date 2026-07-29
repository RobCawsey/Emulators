using System.Text.RegularExpressions;
using GenesisSharp.Cpu68000;

namespace GenesisSharp.Frontend;

/// <summary>Standalone 68000 disassembler for the debug window's "next instruction" readout --
/// entirely separate from <c>M68000</c>'s own decode/execute logic (this never runs during
/// normal emulation, only when the debug window happens to be open), so a gap or mistake here
/// can't affect emulation correctness. Covers the instruction groups this emulator itself
/// implements; anything it doesn't recognize falls back to a raw hex dump rather than guessing,
/// which is the honest thing to show in a debugger rather than a plausible-looking wrong
/// mnemonic.</summary>
public static class M68kDisassembler
{
    private static readonly string[] SizeSuffix = { "B", "W", "L" };
    private static readonly string[] Conditions =
    {
        "T", "F", "HI", "LS", "CC", "CS", "NE", "EQ", "VC", "VS", "PL", "MI", "GE", "LT", "GT", "LE",
    };

    /// <summary>Addresses this emulator's own <c>GenesisConsole</c> gives specific meaning to
    /// (see its <c>Cpu68000.IBus</c> implementation) -- taken directly from that decode logic
    /// rather than general Genesis lore, so these names are guaranteed to match what this
    /// emulator actually does with them, even where that differs from real hardware's fuller
    /// register set (e.g. the serial I/O registers, which this emulator accepts but ignores,
    /// aren't listed since they're not "real" here).</summary>
    private static readonly Dictionary<uint, string> HardwareRegisters = new()
    {
        [0xC00000] = "VDP_DATA", [0xC00002] = "VDP_DATA",
        [0xC00004] = "VDP_CONTROL", [0xC00006] = "VDP_CONTROL",
        [0xC00008] = "VDP_HV_COUNTER",
        [0xC00011] = "PSG",
        [0xA10000] = "VERSION", [0xA10001] = "VERSION",
        [0xA10002] = "CTRL1_DATA", [0xA10003] = "CTRL1_DATA",
        [0xA10004] = "CTRL2_DATA", [0xA10005] = "CTRL2_DATA",
        [0xA10006] = "EXT_DATA", [0xA10007] = "EXT_DATA",
        [0xA10008] = "CTRL1_DIR", [0xA10009] = "CTRL1_DIR",
        [0xA1000A] = "CTRL2_DIR", [0xA1000B] = "CTRL2_DIR",
        [0xA1000C] = "EXT_DIR", [0xA1000D] = "EXT_DIR",
        [0xA11100] = "Z80_BUSREQ", [0xA11101] = "Z80_BUSREQ",
        [0xA11200] = "Z80_RESET", [0xA11201] = "Z80_RESET",
        [0xA14000] = "TMSS", [0xA14001] = "TMSS", [0xA14002] = "TMSS", [0xA14003] = "TMSS",
    };

    private static readonly Regex HexLiteral = new(@"\$([0-9A-Fa-f]{4,8})", RegexOptions.Compiled);

    /// <summary>Decodes one instruction starting at <paramref name="pc"/>. Never throws --
    /// worst case is the hex-dump fallback.</summary>
    public static DisassembledInstruction Decode(uint pc, IBus bus)
    {
        try
        {
            var reader = new Reader(pc, bus);
            ushort opcode = reader.NextWord();
            string text = AnnotateHardwareRegisters(DecodeOpcode(opcode, reader, pc));
            return new DisassembledInstruction(pc, text, (int)(reader.Pc - pc), reader.BranchTarget);
        }
        catch
        {
            // Any EA combination this disassembler doesn't model (or a read past what it
            // expects) -- fall back to just the opcode word rather than throwing out of a
            // debug-only display path.
            ushort raw = bus.ReadWord(pc);
            return new DisassembledInstruction(pc, $"DC.W ${raw:X4}", 2, null);
        }
    }

    /// <summary>Decodes <paramref name="count"/> instructions in sequence starting at
    /// <paramref name="startAddress"/> -- the raw building block for a scrollable disassembly
    /// listing; label assignment (<see cref="DisassemblyLabeler"/>) is a separate pass over the
    /// result since it needs the whole run to know which targets land inside it.</summary>
    public static List<DisassembledInstruction> DisassembleRange(uint startAddress, int count, IBus bus)
    {
        var results = new List<DisassembledInstruction>(count);
        uint address = startAddress;
        for (int i = 0; i < count; i++)
        {
            var instruction = Decode(address, bus);
            results.Add(instruction);
            address += (uint)Math.Max(2, instruction.Length);
        }

        return results;
    }

    /// <summary>Appends a trailing comment naming any known hardware register address found
    /// anywhere in the formatted operand text -- appended once at the end of the line rather
    /// than substituted inline, since an inline replacement would land in the middle of a
    /// ".W"/".L" size suffix that follows the address in absolute-addressing operands.</summary>
    private static string AnnotateHardwareRegisters(string text)
    {
        List<string>? names = null;
        foreach (Match match in HexLiteral.Matches(text))
        {
            uint value = Convert.ToUInt32(match.Groups[1].Value, 16);
            if (HardwareRegisters.TryGetValue(value, out string? name))
            {
                names ??= new List<string>();
                if (!names.Contains(name))
                {
                    names.Add(name);
                }
            }
        }

        return names is null ? text : $"{text}  ; {string.Join(", ", names)}";
    }

    private sealed class Reader
    {
        private readonly IBus _bus;
        public uint Pc;
        public uint? BranchTarget;
        public Reader(uint pc, IBus bus) { Pc = pc; _bus = bus; }
        public ushort NextWord() { ushort w = _bus.ReadWord(Pc); Pc += 2; return w; }
        public uint NextLong() { uint hi = NextWord(); uint lo = NextWord(); return (hi << 16) | lo; }
    }

    private static string DecodeOpcode(ushort op, Reader r, uint instructionStart)
    {
        int topNibble = (op >> 12) & 0xF;
        return topNibble switch
        {
            0x0 => DecodeGroup0(op, r),
            0x1 => DecodeMove(op, r, 0),
            0x2 => DecodeMove(op, r, 2),
            0x3 => DecodeMove(op, r, 1),
            0x4 => DecodeGroup4(op, r),
            0x5 => DecodeGroup5(op, r, instructionStart),
            0x6 => DecodeBranch(op, r, instructionStart),
            0x7 => $"MOVEQ #{(sbyte)(op & 0xFF)},D{(op >> 9) & 7}",
            0x8 => DecodeGroup8(op, r),
            0x9 => DecodeAddSub("SUB", op, r),
            0xB => DecodeGroupB(op, r),
            0xC => DecodeGroupC(op, r),
            0xD => DecodeAddSub("ADD", op, r),
            0xE => DecodeShiftRotate(op, r),
            _ => $"DC.W ${op:X4}",
        };
    }

    // ---- Effective address decoding ----------------------------------------------------

    private static string DecodeEa(int mode, int reg, int sizeIndex, Reader r)
    {
        switch (mode)
        {
            case 0: return $"D{reg}";
            case 1: return $"A{reg}";
            case 2: return $"(A{reg})";
            case 3: return $"(A{reg})+";
            case 4: return $"-(A{reg})";
            case 5: return $"{(short)r.NextWord()}(A{reg})";
            case 6: return DecodeIndexed(r, $"A{reg}");
            case 7:
                switch (reg)
                {
                    case 0: return $"${r.NextWord():X4}.W";
                    case 1: return $"${r.NextLong():X8}.L";
                    case 2: return $"{(short)r.NextWord()}(PC)";
                    case 3: return DecodeIndexed(r, "PC");
                    case 4: return sizeIndex switch
                    {
                        0 => $"#${r.NextWord() & 0xFF:X2}",
                        2 => $"#${r.NextLong():X8}",
                        _ => $"#${r.NextWord():X4}",
                    };
                }
                break;
        }

        return "???";
    }

    private static string DecodeIndexed(Reader r, string baseReg)
    {
        ushort ext = r.NextWord();
        int indexReg = (ext >> 12) & 7;
        bool isAddressReg = (ext & 0x8000) != 0;
        bool isLongIndex = (ext & 0x0800) != 0;
        sbyte disp = (sbyte)(ext & 0xFF);
        string indexName = (isAddressReg ? "A" : "D") + indexReg + (isLongIndex ? ".L" : ".W");
        return $"{disp}({baseReg},{indexName})";
    }

    private static string SizeIndexFromBits(int bits) => bits switch { 0 => "B", 1 => "W", 2 => "L", _ => "?" };

    // ---- MOVE / MOVEA -------------------------------------------------------------------

    private static string DecodeMove(ushort op, Reader r, int sizeIndex)
    {
        int srcMode = (op >> 3) & 7, srcReg = op & 7;
        int dstMode = (op >> 6) & 7, dstReg = (op >> 9) & 7;
        string src = DecodeEa(srcMode, srcReg, sizeIndex, r);
        if (dstMode == 1)
        {
            return $"MOVEA.{SizeSuffix[sizeIndex]} {src},A{dstReg}";
        }

        string dst = DecodeEa(dstMode, dstReg, sizeIndex, r);
        return $"MOVE.{SizeSuffix[sizeIndex]} {src},{dst}";
    }

    // ---- Group 0: immediate ops, static/dynamic bit ops, MOVEP ---------------------------

    private static string DecodeGroup0(ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if ((op & 0x3F) == 0x3C && (op & 0x0F00) is 0x000 or 0x200 or 0x400 or 0x600 or 0xA00 or 0xC00)
        {
            // ORI/ANDI/EORI to CCR or SR: xxxx000x111111xx with the size field forced to word.
        }

        if ((op & 0xF9C0) == 0x0800 && mode != 1)
        {
            // Dynamic bit op handled below via bit 8 test; static ops use an immediate bit number.
        }

        // Static bit ops: 0000 100 0 SS mmm rrr (bits 8 = 1 marks "static", next 2 bits = op).
        if ((op & 0x0F00) == 0x0800 && mode != 1)
        {
            int subOp = (op >> 6) & 3;
            string bitOpName = subOp switch { 0 => "BTST", 1 => "BCHG", 2 => "BCLR", _ => "BSET" };
            int bitNum = r.NextWord() & 0xFF;
            string ea = DecodeEa(mode, reg, 0, r);
            return $"{bitOpName} #{bitNum},{ea}";
        }

        // Dynamic bit ops: 0000 rrr 1 SS mmm rrr (register holds the bit number).
        if ((op & 0x0100) == 0x0100 && mode != 1)
        {
            int subOp = (op >> 6) & 3;
            string bitOpName = subOp switch { 0 => "BTST", 1 => "BCHG", 2 => "BCLR", _ => "BSET" };
            int bitReg = (op >> 9) & 7;
            string ea = DecodeEa(mode, reg, 0, r);
            return $"{bitOpName} D{bitReg},{ea}";
        }

        // MOVEP: 0000 ddd 1 mm 001 aaa (mm selects direction+size) -- mode field is fixed at 001.
        if (mode == 1 && (op & 0x0100) == 0x0100)
        {
            int dReg = (op >> 9) & 7;
            int opMode = (op >> 6) & 3;
            short disp = (short)r.NextWord();
            string size = (opMode & 1) == 0 ? "W" : "L";
            return (opMode & 2) == 0
                ? $"MOVEP.{size} {disp}(A{reg}),D{dReg}"
                : $"MOVEP.{size} D{dReg},{disp}(A{reg})";
        }

        // Immediate ops: 0000 ooo0 SS mmm rrr where ooo selects ORI/ANDI/SUBI/ADDI/EORI/CMPI.
        int group = (op >> 9) & 7;
        int sizeBits = (op >> 6) & 3;
        if (sizeBits == 3)
        {
            return $"DC.W ${op:X4}";
        }

        string mnemonic = group switch
        {
            0 => "ORI", 1 => "ANDI", 2 => "SUBI", 3 => "ADDI", 5 => "EORI", 6 => "CMPI", _ => null!,
        };
        if (mnemonic is null)
        {
            return $"DC.W ${op:X4}";
        }

        if (mode == 7 && reg == 4)
        {
            // #imm,CCR or #imm,SR (word-sized immediate to the status registers).
            ushort imm = r.NextWord();
            string target = sizeBits == 0 ? "CCR" : "SR";
            return $"{mnemonic} #${imm:X4},{target}";
        }

        string immText = sizeBits switch
        {
            0 => $"#${r.NextWord() & 0xFF:X2}",
            2 => $"#${r.NextLong():X8}",
            _ => $"#${r.NextWord():X4}",
        };
        string dest = DecodeEa(mode, reg, sizeBits, r);
        return $"{mnemonic}.{SizeSuffix[sizeBits]} {immText},{dest}";
    }

    // ---- Group 4: miscellaneous -----------------------------------------------------------

    private static string DecodeGroup4(ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7;

        if (op == 0x4E71) return "NOP";
        if (op == 0x4E70) return "RESET";
        if (op == 0x4E72) { ushort imm = r.NextWord(); return $"STOP #${imm:X4}"; }
        if (op == 0x4E73) return "RTE";
        if (op == 0x4E75) return "RTS";
        if (op == 0x4E76) return "TRAPV";
        if (op == 0x4E77) return "RTR";
        if (op == 0x4AFC) return "ILLEGAL";

        if ((op & 0xFFF0) == 0x4E60) return $"MOVE USP,A{op & 7}";
        if ((op & 0xFFF8) == 0x4E68) return $"MOVE A{op & 7},USP";

        if ((op & 0xFF00) == 0x4E00 && mode == 7 && reg is 0 or 1)
        {
            // Already handled by the exact-opcode checks above; fall through otherwise.
        }

        if ((op & 0xFFC0) == 0x4E80) return DecodeJsrJmp("JSR", mode, reg, r);
        if ((op & 0xFFC0) == 0x4EC0) return DecodeJsrJmp("JMP", mode, reg, r);
        // Mask must exclude bits 11-9 (the destination register field, variable) -- 0xFFC0
        // pinned them to 0 (matching only LEA-to-A0), so e.g. "LEA d16(A7),A7" (0x4FEF, a very
        // common stack-adjustment idiom, destination A7/bits11-9=111) fell through unrecognized
        // as "LEA", got emitted as a raw DC.W by the fallback below, and threw off every
        // subsequent instruction's alignment in a linear disassembly listing (found via a real
        // ROM, Omega Blast, whose boot code uses exactly this idiom).
        if ((op & 0xF1C0) == 0x41C0) return $"LEA {DecodeEa(mode, reg, 2, r)},A{(op >> 9) & 7}";
        if ((op & 0xFFC0) == 0x4840) return $"PEA {DecodeEa(mode, reg, 2, r)}";

        if ((op & 0xFFF8) == 0x4880 || (op & 0xFF00) == 0x4800 && ((op >> 6) & 7) == 0 && mode == 0)
        {
            // handled below as EXT/EXTB via exact masks
        }
        if ((op & 0xFFF8) == 0x4880) return $"EXT.W D{op & 7}";
        if ((op & 0xFFF8) == 0x48C0) return $"EXT.L D{op & 7}";
        if ((op & 0xFFF8) == 0x49C0) return $"EXTB.L D{op & 7}";
        if ((op & 0xFFF8) == 0x4840) return $"SWAP D{op & 7}";

        if ((op & 0xFFC0) == 0x4AC0) return $"TAS {DecodeEa(mode, reg, 0, r)}";

        if ((op & 0xFF00) == 0x4A00)
        {
            int sz = (op >> 6) & 3;
            if (sz != 3) return $"TST.{SizeSuffix[sz]} {DecodeEa(mode, reg, sz, r)}";
        }

        if ((op & 0xFF00) == 0x4000)
        {
            int sz = (op >> 6) & 3;
            if (sz != 3) return $"NEGX.{SizeSuffix[sz]} {DecodeEa(mode, reg, sz, r)}";
        }
        if ((op & 0xFF00) == 0x4200)
        {
            int sz = (op >> 6) & 3;
            if (sz != 3) return $"CLR.{SizeSuffix[sz]} {DecodeEa(mode, reg, sz, r)}";
        }
        if ((op & 0xFF00) == 0x4400)
        {
            int sz = (op >> 6) & 3;
            if (sz != 3) return $"NEG.{SizeSuffix[sz]} {DecodeEa(mode, reg, sz, r)}";
        }
        if ((op & 0xFF00) == 0x4600)
        {
            int sz = (op >> 6) & 3;
            if (sz != 3) return $"NOT.{SizeSuffix[sz]} {DecodeEa(mode, reg, sz, r)}";
        }

        if ((op & 0xFFC0) == 0x40C0) return $"MOVE SR,{DecodeEa(mode, reg, 1, r)}";
        if ((op & 0xFFC0) == 0x42C0) return $"MOVE CCR,{DecodeEa(mode, reg, 1, r)}";
        if ((op & 0xFFC0) == 0x44C0) return $"MOVE {DecodeEa(mode, reg, 1, r)},CCR";
        if ((op & 0xFFC0) == 0x46C0) return $"MOVE {DecodeEa(mode, reg, 1, r)},SR";

        if ((op & 0xF1C0) == 0x4180) return $"CHK {DecodeEa(mode, reg, 1, r)},D{(op >> 9) & 7}";

        if ((op & 0xFB80) == 0x4880)
        {
            // MOVEM register-to-memory (bit 10 = 0) handled by mask below; this branch unused.
        }
        if ((op & 0xFB80) == 0x4880 || (op & 0xFB80) == 0x4C80)
        {
            bool toMemory = (op & 0x0400) == 0;
            bool isLong = (op & 0x0040) != 0;
            ushort mask = r.NextWord();
            string list = DecodeRegisterList(mask, mode == 4);
            string ea = DecodeEa(mode, reg, isLong ? 2 : 1, r);
            return toMemory ? $"MOVEM.{(isLong ? "L" : "W")} {list},{ea}" : $"MOVEM.{(isLong ? "L" : "W")} {ea},{list}";
        }

        if ((op & 0xFFF0) == 0x4E40) return $"TRAP #{op & 0xF}";
        if ((op & 0xFFF8) == 0x4E50) return $"LINK A{op & 7},#{(short)r.NextWord()}";
        if ((op & 0xFFF8) == 0x4E58) return $"UNLK A{op & 7}";

        return $"DC.W ${op:X4}";
    }

    /// <summary>JSR/JMP share the same target-capture logic: only absolute-addressing forms
    /// (mode 7, reg 0 or 1) have a statically known destination -- every other EA mode is
    /// register-indirect or PC-relative-with-runtime-offset, which this disassembler doesn't
    /// resolve to a fixed address.</summary>
    private static string DecodeJsrJmp(string mnemonic, int mode, int reg, Reader r)
    {
        if (mode == 7 && reg is 0 or 1)
        {
            // Absolute-word mode sign-extends its 16-bit operand to a full 32-bit address on
            // real hardware -- matters here since a mismatched target would silently fail to
            // line up with a label even when one exists.
            uint target = reg == 0 ? unchecked((uint)(int)(short)r.NextWord()) : r.NextLong();
            r.BranchTarget = target;
            return $"{mnemonic} ${target:X6}{(reg == 0 ? ".W" : ".L")}";
        }

        return $"{mnemonic} {DecodeEa(mode, reg, 2, r)}";
    }

    private static string DecodeRegisterList(ushort mask, bool predecrement)
    {
        var names = new List<string>();
        for (int i = 0; i < 16; i++)
        {
            // Predecrement mode stores the mask in reverse register order (A7..A0,D7..D0).
            int bit = predecrement ? 15 - i : i;
            if ((mask & (1 << bit)) != 0)
            {
                names.Add(i < 8 ? $"D{i}" : $"A{i - 8}");
            }
        }

        return names.Count == 0 ? "(none)" : string.Join("/", names);
    }

    // ---- Group 5: ADDQ/SUBQ/Scc/DBcc -------------------------------------------------------

    private static string DecodeGroup5(ushort op, Reader r, uint instructionStart)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        int sizeBits = (op >> 6) & 3;

        if (sizeBits == 3)
        {
            int condition = (op >> 8) & 0xF;
            if (mode == 1)
            {
                short disp = (short)r.NextWord();
                // Real 68000 branch displacements are always relative to the address of the
                // opcode word plus 2 (the word's own size), never the opcode's own address --
                // BranchTarget (used for branch-target highlighting elsewhere, e.g. DebugForm)
                // was off by 2 for every DBcc, though the printed "*+N" text itself is unaffected
                // since that displays the raw displacement, not this computed absolute target.
                r.BranchTarget = unchecked((uint)(instructionStart + 2 + disp));
                return $"DB{Conditions[condition]} D{reg},*{(disp >= 0 ? "+" : "")}{disp}";
            }

            return $"S{Conditions[condition]} {DecodeEa(mode, reg, 0, r)}";
        }

        int data = (op >> 9) & 7;
        if (data == 0) data = 8;
        string mnemonic = (op & 0x0100) == 0 ? "ADDQ" : "SUBQ";
        return $"{mnemonic}.{SizeSuffix[sizeBits]} #{data},{DecodeEa(mode, reg, sizeBits, r)}";
    }

    // ---- Group 6: Bcc/BSR/BRA ---------------------------------------------------------------

    private static string DecodeBranch(ushort op, Reader r, uint instructionStart)
    {
        int condition = (op >> 8) & 0xF;
        int disp8 = (sbyte)(op & 0xFF);
        int displacement = disp8;
        if (disp8 == 0)
        {
            displacement = (short)r.NextWord();
        }
        else if (disp8 == -1)
        {
            displacement = (int)r.NextLong();
        }

        // Same +2 base offset as DecodeGroup5's DBcc case above -- real 68000 branch
        // displacements are always relative to the opcode word's address plus 2, regardless of
        // whether the displacement itself came from the opcode's low byte or an extension word.
        r.BranchTarget = unchecked((uint)(instructionStart + 2 + displacement));
        string name = condition switch { 0 => "BRA", 1 => "BSR", _ => $"B{Conditions[condition]}" };
        return $"{name} *{(displacement >= 0 ? "+" : "")}{displacement}";
    }

    // ---- Group 8: OR / DIVU / DIVS / SBCD ---------------------------------------------------

    private static string DecodeGroup8(ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dReg = (op >> 9) & 7;
        int subMode = (op >> 6) & 7;

        if (subMode == 3) return $"DIVU {DecodeEa(mode, reg, 1, r)},D{dReg}";
        if (subMode == 7) return $"DIVS {DecodeEa(mode, reg, 1, r)},D{dReg}";
        if (subMode == 4 && mode == 0) return $"SBCD D{reg},D{dReg}";
        if (subMode == 4 && mode == 1) return $"SBCD -(A{reg}),-(A{dReg})";

        int sizeBits = subMode & 3;
        bool eaIsDest = subMode >= 4;
        string ea = DecodeEa(mode, reg, sizeBits, r);
        return eaIsDest ? $"OR.{SizeSuffix[sizeBits]} D{dReg},{ea}" : $"OR.{SizeSuffix[sizeBits]} {ea},D{dReg}";
    }

    // ---- Group 9/D: ADD/SUB/ADDA/SUBA/ADDX/SUBX ---------------------------------------------

    private static string DecodeAddSub(string baseName, ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dReg = (op >> 9) & 7;
        int subMode = (op >> 6) & 7;

        if (subMode == 3) return $"{baseName}A.W {DecodeEa(mode, reg, 1, r)},A{dReg}";
        if (subMode == 7) return $"{baseName}A.L {DecodeEa(mode, reg, 2, r)},A{dReg}";

        int sizeBits = subMode & 3;
        if (mode is 0 or 1 && (op & 0x0100) != 0 && sizeBits != 3)
        {
            // ADDX/SUBX (register or predecrement forms share the same size-bit pattern as the
            // memory-destination form below; disambiguated by the low 4 bits of the mode field).
            bool isReg = mode == 0;
            string suffix = SizeSuffix[sizeBits];
            return isReg
                ? $"{baseName}X.{suffix} D{reg},D{dReg}"
                : $"{baseName}X.{suffix} -(A{reg}),-(A{dReg})";
        }

        bool eaIsDest = (op & 0x0100) != 0;
        string ea = DecodeEa(mode, reg, sizeBits, r);
        return eaIsDest ? $"{baseName}.{SizeSuffix[sizeBits]} D{dReg},{ea}" : $"{baseName}.{SizeSuffix[sizeBits]} {ea},D{dReg}";
    }

    // ---- Group B: CMP/EOR/CMPA/CMPM ---------------------------------------------------------

    private static string DecodeGroupB(ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dReg = (op >> 9) & 7;
        int subMode = (op >> 6) & 7;

        if (subMode == 3) return $"CMPA.W {DecodeEa(mode, reg, 1, r)},A{dReg}";
        if (subMode == 7) return $"CMPA.L {DecodeEa(mode, reg, 2, r)},A{dReg}";

        int sizeBits = subMode & 3;
        if ((op & 0x0100) != 0 && mode == 1)
        {
            return $"CMPM.{SizeSuffix[sizeBits]} (A{reg})+,(A{dReg})+";
        }

        if ((op & 0x0100) != 0)
        {
            return $"EOR.{SizeSuffix[sizeBits]} D{dReg},{DecodeEa(mode, reg, sizeBits, r)}";
        }

        return $"CMP.{SizeSuffix[sizeBits]} {DecodeEa(mode, reg, sizeBits, r)},D{dReg}";
    }

    // ---- Group C: AND/MULU/MULS/ABCD/EXG -----------------------------------------------------

    private static string DecodeGroupC(ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7, dReg = (op >> 9) & 7;
        int subMode = (op >> 6) & 7;

        if (subMode == 3) return $"MULU {DecodeEa(mode, reg, 1, r)},D{dReg}";
        if (subMode == 7) return $"MULS {DecodeEa(mode, reg, 1, r)},D{dReg}";
        if (subMode == 4 && mode == 0) return $"ABCD D{reg},D{dReg}";
        if (subMode == 4 && mode == 1) return $"ABCD -(A{reg}),-(A{dReg})";

        if (subMode is 5 or 6 && (op & 0x0130) == 0x0100)
        {
            int exgMode = (op >> 3) & 0x1F;
            if (exgMode == 0x08) return $"EXG D{dReg},D{reg}";
            if (exgMode == 0x09) return $"EXG A{dReg},A{reg}";
            if (exgMode == 0x11) return $"EXG D{dReg},A{reg}";
        }

        int sizeBits = subMode & 3;
        bool eaIsDest = subMode >= 4;
        string ea = DecodeEa(mode, reg, sizeBits, r);
        return eaIsDest ? $"AND.{SizeSuffix[sizeBits]} D{dReg},{ea}" : $"AND.{SizeSuffix[sizeBits]} {ea},D{dReg}";
    }

    // ---- Group E: shifts/rotates --------------------------------------------------------------

    private static string DecodeShiftRotate(ushort op, Reader r)
    {
        int mode = (op >> 3) & 7, reg = op & 7;
        int sizeBits = (op >> 6) & 3;

        if (sizeBits == 3)
        {
            // Memory-operand shift/rotate: bits 8-6 select the operation, mode/reg give the EA.
            int subOp = (op >> 9) & 7;
            bool left = (op & 0x0100) != 0;
            string name = ((op >> 9) & 3) switch { 0 => "AS", 1 => "LS", 2 => "ROX", _ => "RO" };
            _ = subOp;
            return $"{name}{(left ? "L" : "R")}.W {DecodeEa(mode, reg, 1, r)}";
        }

        int countOrReg = (op >> 9) & 7;
        bool isRegisterCount = (op & 0x0020) != 0;
        int type = (op >> 3) & 3;
        bool leftDir = (op & 0x0100) != 0;
        string typeName = type switch { 0 => "AS", 1 => "LS", 2 => "ROX", _ => "RO" };
        string dir = leftDir ? "L" : "R";
        string operand = $"D{reg}";
        string countText = isRegisterCount ? $"D{countOrReg}" : $"#{(countOrReg == 0 ? 8 : countOrReg)}";
        return $"{typeName}{dir}.{SizeSuffix[sizeBits]} {countText},{operand}";
    }
}
