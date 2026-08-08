using System.Text.RegularExpressions;
using GenesisSharp.CpuSh2;

namespace GenesisSharp.Frontend;

/// <summary>Standalone SH-2 disassembler for the debug window's 32X tab -- entirely separate from
/// <c>Sh2</c>'s own decode/execute logic (debug-display-only, never runs during normal emulation).
/// Mirrors <see cref="M68kDisassembler"/>/<see cref="Z80Disassembler"/>'s shape exactly: a single
/// <see cref="Decode"/> entry point that never throws (falls back to a raw hex dump), a
/// <see cref="DisassembleRange"/> building block for a scrollable listing, and a trailing-comment
/// hardware-register annotation pass.
///
/// Opcode groupings and per-instruction bit-field layout (which nibble is Rn vs Rm vs a
/// displacement) are taken directly from <c>Sh2.Decode.cs</c>'s own dispatch tables and each
/// instruction's implementation in the <c>Sh2.*.cs</c> files -- this is a from-scratch encoder of
/// the exact same opcode map the core itself decodes, not an independent transcription of the
/// SH-2 ISA. Displacement operands are shown already byte-scaled (e.g. <c>@(12,R3)</c> for a
/// disp4 of 3 in a MOV.L form), matching the official Hitachi/Renesas assembly mnemonic
/// convention, not the raw encoded nibble/byte. PC-relative loads (MOV.W/L @(disp,PC) and MOVA)
/// resolve to a concrete address using the same "PC as it stands right after this instruction's
/// own fetch, plus 2" convention <c>Sh2.DataTransfer.cs</c> itself uses, since the address is
/// statically known at disassembly time even though the loaded *value* isn't.
///
/// Every instruction PicoDrive/this core's own decode tables treat as architecturally illegal
/// (see <c>Sh2.Decode.cs</c>'s explicit ILLEGAL case lists) is reported as literal "ILLEGAL" text
/// here -- a known fact about the opcode, not a gap in this disassembler -- while a genuine
/// decode-time exception (e.g. a bus read failing at the edge of mapped memory) falls back to a
/// raw <c>.WORD</c> hex dump instead, mirroring the other two disassemblers' fallback shape.</summary>
public static class Sh2Disassembler
{
    /// <summary>The 32X's own SH-2-side register windows (CS0, confirmed against
    /// <c>Sega32XSh2Bus</c>'s own decode: <c>AdapterRegLow/High</c>, <c>VdpRegLow/High</c>,
    /// <c>PaletteLow/High</c>) -- the same three blocks that bus already recognizes, given names
    /// here purely for a trailing disassembly comment.</summary>
    private static readonly (uint Start, uint End, string Name)[] HardwareRegisters =
    {
        (0x4000, 0x403F, "32X_CTRL"),
        (0x4100, 0x411F, "32X_VDP_REGS"),
        (0x4200, 0x43FF, "32X_PALETTE"),
    };

    private static readonly Regex HexLiteral = new(@"\$([0-9A-Fa-f]{2,8})", RegexOptions.Compiled);

    public static DisassembledInstruction Decode(uint pc, IBus bus)
    {
        try
        {
            var reader = new Reader(pc, bus);
            ushort opcode = reader.NextWord();
            string text = AnnotateHardwareRegisters(DecodeOpcode(opcode, reader));
            return new DisassembledInstruction(pc, text, 2, reader.BranchTarget);
        }
        catch
        {
            ushort raw = bus.ReadWord(pc);
            return new DisassembledInstruction(pc, $".WORD ${raw:X4}", 2, null);
        }
    }

    /// <summary>Decodes <paramref name="count"/> instructions in sequence starting at
    /// <paramref name="startAddress"/> -- every SH-2 instruction is exactly one 16-bit word (no
    /// prefix bytes, no variable-length forms), so this always advances by 2, unlike the 68000/
    /// Z80 disassemblers' variable per-instruction length.</summary>
    public static List<DisassembledInstruction> DisassembleRange(uint startAddress, int count, IBus bus)
    {
        var results = new List<DisassembledInstruction>(count);
        uint address = startAddress;
        for (int i = 0; i < count; i++)
        {
            var instruction = Decode(address, bus);
            results.Add(instruction);
            address += (uint)Math.Max(1, instruction.Length);
        }

        return results;
    }

    private static string AnnotateHardwareRegisters(string text)
    {
        List<string>? names = null;
        foreach (Match match in HexLiteral.Matches(text))
        {
            uint value = Convert.ToUInt32(match.Groups[1].Value, 16);
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
        public uint Pc;
        public uint? BranchTarget;
        public Reader(uint pc, IBus bus) { Pc = pc; _bus = bus; }
        public ushort NextWord() { ushort value = _bus.ReadWord(Pc); Pc += 2; return value; }
    }

    private static string Reg(int index) => $"R{index}";
    private static string Imm8(int value) => $"#${value & 0xFF:X2}";
    private static int SignExtend8(int value) => (sbyte)value;
    private static int SignExtend12(int value) => (value << 20) >> 20;
    private static uint Disp12Target(uint pcAfterOpcode, int disp12) => pcAfterOpcode + (uint)(SignExtend12(disp12) * 2) + 2;
    private static uint Disp8Target(uint pcAfterOpcode, int disp8) => pcAfterOpcode + (uint)(SignExtend8(disp8) * 2) + 2;

    private static string DecodeOpcode(ushort opcode, Reader r)
    {
        int n = (opcode >> 8) & 0xF;
        int m = (opcode >> 4) & 0xF;

        switch (opcode >> 12)
        {
            case 0b0000: return DecodeGroup0(opcode, n, m);
            case 0b0001: return $"MOV.L {Reg(m)},@({(opcode & 0xF) * 4},{Reg(n)})";
            case 0b0010: return DecodeGroup2(opcode, n, m);
            case 0b0011: return DecodeGroup3(opcode, n, m);
            case 0b0100: return DecodeGroup4(opcode, n, m);
            case 0b0101: return $"MOV.L @({(opcode & 0xF) * 4},{Reg(m)}),{Reg(n)}";
            case 0b0110: return DecodeGroup6(opcode, n, m);
            case 0b0111: return $"ADD {Imm8(opcode & 0xFF)},{Reg(n)}";
            case 0b1000: return DecodeGroup8(opcode, r);
            case 0b1001:
                {
                    uint addr = r.Pc + (uint)(opcode & 0xFF) * 2 + 2;
                    return $"MOV.W @(${addr:X8}),{Reg(n)}";
                }
            case 0b1010:
                {
                    uint target = Disp12Target(r.Pc, opcode & 0xFFF);
                    r.BranchTarget = target;
                    return $"BRA ${target:X8}";
                }
            case 0b1011:
                {
                    uint target = Disp12Target(r.Pc, opcode & 0xFFF);
                    r.BranchTarget = target;
                    return $"BSR ${target:X8}";
                }
            case 0b1100: return DecodeGroupC(opcode, r);
            case 0b1101:
                {
                    uint addr = ((r.Pc + 2) & ~3u) + (uint)(opcode & 0xFF) * 4;
                    return $"MOV.L @(${addr:X8}),{Reg(n)}";
                }
            case 0b1110: return $"MOV {Imm8(opcode & 0xFF)},{Reg(n)}";
            default: return "ILLEGAL"; // 0b1111 -- no FPU in this core, whole nibble is illegal
        }
    }

    /// <summary>Top nibble 0000, switch on the low 6 bits -- mirrors <c>Sh2.Decode.cs</c>'s
    /// <c>ExecuteGroup0</c> exactly, including its explicit ILLEGAL case list.</summary>
    private static string DecodeGroup0(ushort opcode, int n, int m)
    {
        switch (opcode & 0x3F)
        {
            case 0x02: return $"STC SR,{Reg(n)}";
            case 0x03: return $"BSRF {Reg(n)}";
            case 0x04: case 0x14: case 0x24: case 0x34: return $"MOV.B {Reg(m)},@(R0,{Reg(n)})";
            case 0x05: case 0x15: case 0x25: case 0x35: return $"MOV.W {Reg(m)},@(R0,{Reg(n)})";
            case 0x06: case 0x16: case 0x26: case 0x36: return $"MOV.L {Reg(m)},@(R0,{Reg(n)})";
            case 0x08: return "CLRT";
            case 0x09: return "NOP";
            case 0x0A: return $"STS MACH,{Reg(n)}";
            case 0x0B: return "RTS";
            case 0x07: case 0x17: case 0x27: case 0x37: return $"MUL.L {Reg(m)},{Reg(n)}";
            case 0x0C: case 0x1C: case 0x2C: case 0x3C: return $"MOV.B @(R0,{Reg(m)}),{Reg(n)}";
            case 0x0D: case 0x1D: case 0x2D: case 0x3D: return $"MOV.W @(R0,{Reg(m)}),{Reg(n)}";
            case 0x0E: case 0x1E: case 0x2E: case 0x3E: return $"MOV.L @(R0,{Reg(m)}),{Reg(n)}";
            case 0x0F: case 0x1F: case 0x2F: case 0x3F: return $"MAC.L @{Reg(m)}+,@{Reg(n)}+";
            case 0x12: return $"STC GBR,{Reg(n)}";
            case 0x18: return "SETT";
            case 0x19: return "DIV0U";
            case 0x1A: return $"STS MACL,{Reg(n)}";
            case 0x1B: return "SLEEP";
            case 0x22: return $"STC VBR,{Reg(n)}";
            case 0x23: return $"BRAF {Reg(n)}";
            case 0x28: return "CLRMAC";
            case 0x29: return $"MOVT {Reg(n)}";
            case 0x2A: return $"STS PR,{Reg(n)}";
            case 0x2B: return "RTE";
            default: return "ILLEGAL";
        }
    }

    /// <summary>Top nibble 0010. Mirrors <c>ExecuteGroup2</c>.</summary>
    private static string DecodeGroup2(ushort opcode, int n, int m)
    {
        return (opcode & 0xF) switch
        {
            0x0 => $"MOV.B {Reg(m)},@{Reg(n)}",
            0x1 => $"MOV.W {Reg(m)},@{Reg(n)}",
            0x2 => $"MOV.L {Reg(m)},@{Reg(n)}",
            0x4 => $"MOV.B {Reg(m)},@-{Reg(n)}",
            0x5 => $"MOV.W {Reg(m)},@-{Reg(n)}",
            0x6 => $"MOV.L {Reg(m)},@-{Reg(n)}",
            0x7 => $"DIV0S {Reg(m)},{Reg(n)}",
            0x8 => $"TST {Reg(m)},{Reg(n)}",
            0x9 => $"AND {Reg(m)},{Reg(n)}",
            0xA => $"XOR {Reg(m)},{Reg(n)}",
            0xB => $"OR {Reg(m)},{Reg(n)}",
            0xC => $"CMP/STR {Reg(m)},{Reg(n)}",
            0xD => $"XTRCT {Reg(m)},{Reg(n)}",
            0xE => $"MULU.W {Reg(m)},{Reg(n)}",
            0xF => $"MULS.W {Reg(m)},{Reg(n)}",
            _ => "ILLEGAL", // 0x3 -- unreachable given the mask, listed for completeness
        };
    }

    /// <summary>Top nibble 0011. Mirrors <c>ExecuteGroup3</c>.</summary>
    private static string DecodeGroup3(ushort opcode, int n, int m)
    {
        return (opcode & 0xF) switch
        {
            0x0 => $"CMP/EQ {Reg(m)},{Reg(n)}",
            0x2 => $"CMP/HS {Reg(m)},{Reg(n)}",
            0x3 => $"CMP/GE {Reg(m)},{Reg(n)}",
            0x4 => $"DIV1 {Reg(m)},{Reg(n)}",
            0x5 => $"DMULU.L {Reg(m)},{Reg(n)}",
            0x6 => $"CMP/HI {Reg(m)},{Reg(n)}",
            0x7 => $"CMP/GT {Reg(m)},{Reg(n)}",
            0x8 => $"SUB {Reg(m)},{Reg(n)}",
            0xA => $"SUBC {Reg(m)},{Reg(n)}",
            0xB => $"SUBV {Reg(m)},{Reg(n)}",
            0xC => $"ADD {Reg(m)},{Reg(n)}",
            0xD => $"DMULS.L {Reg(m)},{Reg(n)}",
            0xE => $"ADDC {Reg(m)},{Reg(n)}",
            0xF => $"ADDV {Reg(m)},{Reg(n)}",
            _ => "ILLEGAL", // 0x1/0x9
        };
    }

    /// <summary>Top nibble 0100, switch on the low 6 bits. Mirrors <c>ExecuteGroup4</c>: the
    /// single register operand for every case except MAC.W is bits 8-11 (<paramref name="n"/>);
    /// MAC.W alone additionally uses bits 4-7 (<paramref name="m"/>) as its second pointer
    /// register.</summary>
    private static string DecodeGroup4(ushort opcode, int n, int m)
    {
        switch (opcode & 0x3F)
        {
            case 0x00: return $"SHLL {Reg(n)}";
            case 0x01: return $"SHLR {Reg(n)}";
            case 0x02: return $"STS.L MACH,@-{Reg(n)}";
            case 0x03: return $"STC.L SR,@-{Reg(n)}";
            case 0x04: return $"ROTL {Reg(n)}";
            case 0x05: return $"ROTR {Reg(n)}";
            case 0x06: return $"LDS.L @{Reg(n)}+,MACH";
            case 0x07: return $"LDC.L @{Reg(n)}+,SR";
            case 0x08: return $"SHLL2 {Reg(n)}";
            case 0x09: return $"SHLR2 {Reg(n)}";
            case 0x0A: return $"LDS {Reg(n)},MACH";
            case 0x0B: return $"JSR @{Reg(n)}";
            case 0x0E: return $"LDC {Reg(n)},SR";
            case 0x10: return $"DT {Reg(n)}";
            case 0x11: return $"CMP/PZ {Reg(n)}";
            case 0x12: return $"STS.L MACL,@-{Reg(n)}";
            case 0x13: return $"STC.L GBR,@-{Reg(n)}";
            case 0x15: return $"CMP/PL {Reg(n)}";
            case 0x16: return $"LDS.L @{Reg(n)}+,MACL";
            case 0x17: return $"LDC.L @{Reg(n)}+,GBR";
            case 0x18: return $"SHLL8 {Reg(n)}";
            case 0x19: return $"SHLR8 {Reg(n)}";
            case 0x1A: return $"LDS {Reg(n)},MACL";
            case 0x1B: return $"TAS.B @{Reg(n)}";
            case 0x1E: return $"LDC {Reg(n)},GBR";
            case 0x20: return $"SHAL {Reg(n)}";
            case 0x21: return $"SHAR {Reg(n)}";
            case 0x22: return $"STS.L PR,@-{Reg(n)}";
            case 0x23: return $"STC.L VBR,@-{Reg(n)}";
            case 0x24: return $"ROTCL {Reg(n)}";
            case 0x25: return $"ROTCR {Reg(n)}";
            case 0x26: return $"LDS.L @{Reg(n)}+,PR";
            case 0x27: return $"LDC.L @{Reg(n)}+,VBR";
            case 0x28: return $"SHLL16 {Reg(n)}";
            case 0x29: return $"SHLR16 {Reg(n)}";
            case 0x2A: return $"LDS {Reg(n)},PR";
            case 0x2B: return $"JMP @{Reg(n)}";
            case 0x2E: return $"LDC {Reg(n)},VBR";
            case 0x0F: case 0x1F: case 0x2F: case 0x3F: return $"MAC.W @{Reg(m)}+,@{Reg(n)}+";
            default: return "ILLEGAL";
        }
    }

    /// <summary>Top nibble 0110. Mirrors <c>ExecuteGroup6</c>.</summary>
    private static string DecodeGroup6(ushort opcode, int n, int m)
    {
        return (opcode & 0xF) switch
        {
            0x0 => $"MOV.B @{Reg(m)},{Reg(n)}",
            0x1 => $"MOV.W @{Reg(m)},{Reg(n)}",
            0x2 => $"MOV.L @{Reg(m)},{Reg(n)}",
            0x3 => $"MOV {Reg(m)},{Reg(n)}",
            0x4 => $"MOV.B @{Reg(m)}+,{Reg(n)}",
            0x5 => $"MOV.W @{Reg(m)}+,{Reg(n)}",
            0x6 => $"MOV.L @{Reg(m)}+,{Reg(n)}",
            0x7 => $"NOT {Reg(m)},{Reg(n)}",
            0x8 => $"SWAP.B {Reg(m)},{Reg(n)}",
            0x9 => $"SWAP.W {Reg(m)},{Reg(n)}",
            0xA => $"NEGC {Reg(m)},{Reg(n)}",
            0xB => $"NEG {Reg(m)},{Reg(n)}",
            0xC => $"EXTU.B {Reg(m)},{Reg(n)}",
            0xD => $"EXTU.W {Reg(m)},{Reg(n)}",
            0xE => $"EXTS.B {Reg(m)},{Reg(n)}",
            0xF => $"EXTS.W {Reg(m)},{Reg(n)}",
            _ => "ILLEGAL", // unreachable: 0x0-0xF all handled above
        };
    }

    /// <summary>Top nibble 1000, sub-dispatched on bits 8-11 (bits 4-7 are the one register
    /// operand, bits 0-3/0-7 the displacement/immediate) -- mirrors <c>ExecuteGroup8</c>. The B/W
    /// forms are hardwired to R0 on one side (source for the store forms, destination for the
    /// load forms), matching <c>Sh2.DataTransfer.cs</c>'s <c>ExecuteMovBS4</c>/<c>ExecuteMovBL4</c>
    /// family exactly.</summary>
    private static string DecodeGroup8(ushort opcode, Reader r)
    {
        int regM = (opcode >> 4) & 0xF;
        int disp4 = opcode & 0xF;
        int disp8 = opcode & 0xFF;

        switch ((opcode >> 8) & 0xF)
        {
            case 0x0: return $"MOV.B R0,@({disp4},{Reg(regM)})";
            case 0x1: return $"MOV.W R0,@({disp4 * 2},{Reg(regM)})";
            case 0x4: return $"MOV.B @({disp4},{Reg(regM)}),R0";
            case 0x5: return $"MOV.W @({disp4 * 2},{Reg(regM)}),R0";
            case 0x8: return $"CMP/EQ {Imm8(disp8)},R0";
            case 0x9:
                {
                    uint target = Disp8Target(r.Pc, disp8);
                    r.BranchTarget = target;
                    return $"BT ${target:X8}";
                }
            case 0xB:
                {
                    uint target = Disp8Target(r.Pc, disp8);
                    r.BranchTarget = target;
                    return $"BF ${target:X8}";
                }
            case 0xD:
                {
                    uint target = Disp8Target(r.Pc, disp8);
                    r.BranchTarget = target;
                    return $"BT/S ${target:X8}";
                }
            case 0xF:
                {
                    uint target = Disp8Target(r.Pc, disp8);
                    r.BranchTarget = target;
                    return $"BF/S ${target:X8}";
                }
            default: return "ILLEGAL"; // 0x2/0x3/0x6/0x7/0xA/0xC/0xE
        }
    }

    /// <summary>Top nibble 1100, sub-dispatched on bits 8-11 -- mirrors <c>ExecuteGroupC</c>. Every
    /// case here operates on R0/GBR/PC only; there is no Rm/Rn register field anywhere in this
    /// nibble.</summary>
    private static string DecodeGroupC(ushort opcode, Reader r)
    {
        int disp8 = opcode & 0xFF;

        switch ((opcode >> 8) & 0xF)
        {
            case 0x0: return $"MOV.B R0,@({disp8},GBR)";
            case 0x1: return $"MOV.W R0,@({disp8 * 2},GBR)";
            case 0x2: return $"MOV.L R0,@({disp8 * 4},GBR)";
            case 0x3: return $"TRAPA {Imm8(disp8)}";
            case 0x4: return $"MOV.B @({disp8},GBR),R0";
            case 0x5: return $"MOV.W @({disp8 * 2},GBR),R0";
            case 0x6: return $"MOV.L @({disp8 * 4},GBR),R0";
            case 0x7:
                {
                    uint addr = ((r.Pc + 2) & ~3u) + (uint)disp8 * 4;
                    return $"MOVA @(${addr:X8}),R0";
                }
            case 0x8: return $"TST {Imm8(disp8)},R0";
            case 0x9: return $"AND {Imm8(disp8)},R0";
            case 0xA: return $"XOR {Imm8(disp8)},R0";
            case 0xB: return $"OR {Imm8(disp8)},R0";
            case 0xC: return $"TST.B {Imm8(disp8)},@(R0,GBR)";
            case 0xD: return $"AND.B {Imm8(disp8)},@(R0,GBR)";
            case 0xE: return $"XOR.B {Imm8(disp8)},@(R0,GBR)";
            default: return $"OR.B {Imm8(disp8)},@(R0,GBR)"; // 0xF
        }
    }
}
