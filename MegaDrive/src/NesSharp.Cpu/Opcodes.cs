namespace NesSharp.Cpu;

/// <summary>How an opcode's addressing-mode operand feeds the operation, which determines
/// the shape of its final micro-op cycle(s).</summary>
public enum OpKind
{
    Read,     // operand fetched from memory/immediate, operation consumes the byte (LDA, ADC, CMP, BIT, ...)
    Write,    // operation supplies a byte (from a register), addressing mode writes it (STA, STX, STY)
    Modify,   // read-modify-write, either on memory or (Accumulator mode) directly on A (ASL, INC, ...)
    Implied,  // no operand; operation acts on registers only (INX, TAX, CLC, ...)
    Branch,   // relative addressing with a taken/not-taken condition (BCC, BEQ, ...)
}

public delegate void ReadOp(Cpu6502 cpu, byte value);
public delegate byte WriteOp(Cpu6502 cpu);
public delegate byte ModifyOp(Cpu6502 cpu, byte value);
public delegate void ImpliedOp(Cpu6502 cpu);
public delegate bool BranchCondition(Cpu6502 cpu);

public readonly struct OpcodeInfo
{
    public readonly string Mnemonic;
    public readonly AddressingMode Mode;
    public readonly OpKind Kind;
    public readonly ReadOp? ReadOperation;
    public readonly WriteOp? WriteOperation;
    public readonly ModifyOp? ModifyOperation;
    public readonly ImpliedOp? ImpliedOperation;
    public readonly BranchCondition? Branch;

    private OpcodeInfo(string mnemonic, AddressingMode mode, OpKind kind,
        ReadOp? readOp, WriteOp? writeOp, ModifyOp? modifyOp, ImpliedOp? impliedOp, BranchCondition? branch)
    {
        Mnemonic = mnemonic;
        Mode = mode;
        Kind = kind;
        ReadOperation = readOp;
        WriteOperation = writeOp;
        ModifyOperation = modifyOp;
        ImpliedOperation = impliedOp;
        Branch = branch;
    }

    public static OpcodeInfo ForRead(string mnemonic, AddressingMode mode, ReadOp op) =>
        new(mnemonic, mode, OpKind.Read, op, null, null, null, null);

    public static OpcodeInfo ForWrite(string mnemonic, AddressingMode mode, WriteOp op) =>
        new(mnemonic, mode, OpKind.Write, null, op, null, null, null);

    public static OpcodeInfo ForModify(string mnemonic, AddressingMode mode, ModifyOp op) =>
        new(mnemonic, mode, OpKind.Modify, null, null, op, null, null);

    public static OpcodeInfo ForImplied(string mnemonic, ImpliedOp op) =>
        new(mnemonic, AddressingMode.Implied, OpKind.Implied, null, null, null, op, null);

    public static OpcodeInfo ForBranch(string mnemonic, BranchCondition condition) =>
        new(mnemonic, AddressingMode.Relative, OpKind.Branch, null, null, null, null, condition);
}

public sealed class UnimplementedOpcodeException : Exception
{
    public byte Opcode { get; }

    public UnimplementedOpcodeException(byte opcode)
        : base($"Opcode 0x{opcode:X2} is not implemented. This is almost certainly an " +
               "undocumented/illegal 6502 opcode — official opcodes only are implemented so far.")
    {
        Opcode = opcode;
    }
}

/// <summary>
/// The 256-entry official-opcode dispatch table. Ten control-flow opcodes (BRK, JSR, RTS,
/// RTI, JMP abs/ind, PHA, PHP, PLA, PLP) are intercepted directly in Cpu6502.FetchAndDecode
/// before this table is consulted, since their cycle sequences don't fit the generic
/// Read/Write/Modify/Implied/Branch template shapes. Every other slot is either an official
/// opcode entry or null (undocumented/illegal — a later milestone).
/// </summary>
public static class OpcodeTable
{
    public static readonly OpcodeInfo?[] Table = Build();

    private static OpcodeInfo?[] Build()
    {
        var t = new OpcodeInfo?[256];

        void R(byte op, string mnemonic, AddressingMode mode, ReadOp fn) => t[op] = OpcodeInfo.ForRead(mnemonic, mode, fn);
        void W(byte op, string mnemonic, AddressingMode mode, WriteOp fn) => t[op] = OpcodeInfo.ForWrite(mnemonic, mode, fn);
        void M(byte op, string mnemonic, AddressingMode mode, ModifyOp fn) => t[op] = OpcodeInfo.ForModify(mnemonic, mode, fn);
        void I(byte op, string mnemonic, ImpliedOp fn) => t[op] = OpcodeInfo.ForImplied(mnemonic, fn);
        void B(byte op, string mnemonic, BranchCondition fn) => t[op] = OpcodeInfo.ForBranch(mnemonic, fn);

        // LDA
        R(0xA9, "LDA", AddressingMode.Immediate, Cpu6502.OpLDA);
        R(0xA5, "LDA", AddressingMode.ZeroPage, Cpu6502.OpLDA);
        R(0xB5, "LDA", AddressingMode.ZeroPageX, Cpu6502.OpLDA);
        R(0xAD, "LDA", AddressingMode.Absolute, Cpu6502.OpLDA);
        R(0xBD, "LDA", AddressingMode.AbsoluteX, Cpu6502.OpLDA);
        R(0xB9, "LDA", AddressingMode.AbsoluteY, Cpu6502.OpLDA);
        R(0xA1, "LDA", AddressingMode.IndirectX, Cpu6502.OpLDA);
        R(0xB1, "LDA", AddressingMode.IndirectY, Cpu6502.OpLDA);

        // LDX
        R(0xA2, "LDX", AddressingMode.Immediate, Cpu6502.OpLDX);
        R(0xA6, "LDX", AddressingMode.ZeroPage, Cpu6502.OpLDX);
        R(0xB6, "LDX", AddressingMode.ZeroPageY, Cpu6502.OpLDX);
        R(0xAE, "LDX", AddressingMode.Absolute, Cpu6502.OpLDX);
        R(0xBE, "LDX", AddressingMode.AbsoluteY, Cpu6502.OpLDX);

        // LDY
        R(0xA0, "LDY", AddressingMode.Immediate, Cpu6502.OpLDY);
        R(0xA4, "LDY", AddressingMode.ZeroPage, Cpu6502.OpLDY);
        R(0xB4, "LDY", AddressingMode.ZeroPageX, Cpu6502.OpLDY);
        R(0xAC, "LDY", AddressingMode.Absolute, Cpu6502.OpLDY);
        R(0xBC, "LDY", AddressingMode.AbsoluteX, Cpu6502.OpLDY);

        // STA
        W(0x85, "STA", AddressingMode.ZeroPage, Cpu6502.OpSTA);
        W(0x95, "STA", AddressingMode.ZeroPageX, Cpu6502.OpSTA);
        W(0x8D, "STA", AddressingMode.Absolute, Cpu6502.OpSTA);
        W(0x9D, "STA", AddressingMode.AbsoluteX, Cpu6502.OpSTA);
        W(0x99, "STA", AddressingMode.AbsoluteY, Cpu6502.OpSTA);
        W(0x81, "STA", AddressingMode.IndirectX, Cpu6502.OpSTA);
        W(0x91, "STA", AddressingMode.IndirectY, Cpu6502.OpSTA);

        // STX / STY
        W(0x86, "STX", AddressingMode.ZeroPage, Cpu6502.OpSTX);
        W(0x96, "STX", AddressingMode.ZeroPageY, Cpu6502.OpSTX);
        W(0x8E, "STX", AddressingMode.Absolute, Cpu6502.OpSTX);
        W(0x84, "STY", AddressingMode.ZeroPage, Cpu6502.OpSTY);
        W(0x94, "STY", AddressingMode.ZeroPageX, Cpu6502.OpSTY);
        W(0x8C, "STY", AddressingMode.Absolute, Cpu6502.OpSTY);

        // ADC
        R(0x69, "ADC", AddressingMode.Immediate, Cpu6502.OpADC);
        R(0x65, "ADC", AddressingMode.ZeroPage, Cpu6502.OpADC);
        R(0x75, "ADC", AddressingMode.ZeroPageX, Cpu6502.OpADC);
        R(0x6D, "ADC", AddressingMode.Absolute, Cpu6502.OpADC);
        R(0x7D, "ADC", AddressingMode.AbsoluteX, Cpu6502.OpADC);
        R(0x79, "ADC", AddressingMode.AbsoluteY, Cpu6502.OpADC);
        R(0x61, "ADC", AddressingMode.IndirectX, Cpu6502.OpADC);
        R(0x71, "ADC", AddressingMode.IndirectY, Cpu6502.OpADC);

        // SBC
        R(0xE9, "SBC", AddressingMode.Immediate, Cpu6502.OpSBC);
        R(0xE5, "SBC", AddressingMode.ZeroPage, Cpu6502.OpSBC);
        R(0xF5, "SBC", AddressingMode.ZeroPageX, Cpu6502.OpSBC);
        R(0xED, "SBC", AddressingMode.Absolute, Cpu6502.OpSBC);
        R(0xFD, "SBC", AddressingMode.AbsoluteX, Cpu6502.OpSBC);
        R(0xF9, "SBC", AddressingMode.AbsoluteY, Cpu6502.OpSBC);
        R(0xE1, "SBC", AddressingMode.IndirectX, Cpu6502.OpSBC);
        R(0xF1, "SBC", AddressingMode.IndirectY, Cpu6502.OpSBC);

        // AND
        R(0x29, "AND", AddressingMode.Immediate, Cpu6502.OpAND);
        R(0x25, "AND", AddressingMode.ZeroPage, Cpu6502.OpAND);
        R(0x35, "AND", AddressingMode.ZeroPageX, Cpu6502.OpAND);
        R(0x2D, "AND", AddressingMode.Absolute, Cpu6502.OpAND);
        R(0x3D, "AND", AddressingMode.AbsoluteX, Cpu6502.OpAND);
        R(0x39, "AND", AddressingMode.AbsoluteY, Cpu6502.OpAND);
        R(0x21, "AND", AddressingMode.IndirectX, Cpu6502.OpAND);
        R(0x31, "AND", AddressingMode.IndirectY, Cpu6502.OpAND);

        // ORA
        R(0x09, "ORA", AddressingMode.Immediate, Cpu6502.OpORA);
        R(0x05, "ORA", AddressingMode.ZeroPage, Cpu6502.OpORA);
        R(0x15, "ORA", AddressingMode.ZeroPageX, Cpu6502.OpORA);
        R(0x0D, "ORA", AddressingMode.Absolute, Cpu6502.OpORA);
        R(0x1D, "ORA", AddressingMode.AbsoluteX, Cpu6502.OpORA);
        R(0x19, "ORA", AddressingMode.AbsoluteY, Cpu6502.OpORA);
        R(0x01, "ORA", AddressingMode.IndirectX, Cpu6502.OpORA);
        R(0x11, "ORA", AddressingMode.IndirectY, Cpu6502.OpORA);

        // EOR
        R(0x49, "EOR", AddressingMode.Immediate, Cpu6502.OpEOR);
        R(0x45, "EOR", AddressingMode.ZeroPage, Cpu6502.OpEOR);
        R(0x55, "EOR", AddressingMode.ZeroPageX, Cpu6502.OpEOR);
        R(0x4D, "EOR", AddressingMode.Absolute, Cpu6502.OpEOR);
        R(0x5D, "EOR", AddressingMode.AbsoluteX, Cpu6502.OpEOR);
        R(0x59, "EOR", AddressingMode.AbsoluteY, Cpu6502.OpEOR);
        R(0x41, "EOR", AddressingMode.IndirectX, Cpu6502.OpEOR);
        R(0x51, "EOR", AddressingMode.IndirectY, Cpu6502.OpEOR);

        // CMP
        R(0xC9, "CMP", AddressingMode.Immediate, Cpu6502.OpCMP);
        R(0xC5, "CMP", AddressingMode.ZeroPage, Cpu6502.OpCMP);
        R(0xD5, "CMP", AddressingMode.ZeroPageX, Cpu6502.OpCMP);
        R(0xCD, "CMP", AddressingMode.Absolute, Cpu6502.OpCMP);
        R(0xDD, "CMP", AddressingMode.AbsoluteX, Cpu6502.OpCMP);
        R(0xD9, "CMP", AddressingMode.AbsoluteY, Cpu6502.OpCMP);
        R(0xC1, "CMP", AddressingMode.IndirectX, Cpu6502.OpCMP);
        R(0xD1, "CMP", AddressingMode.IndirectY, Cpu6502.OpCMP);

        // CPX / CPY / BIT
        R(0xE0, "CPX", AddressingMode.Immediate, Cpu6502.OpCPX);
        R(0xE4, "CPX", AddressingMode.ZeroPage, Cpu6502.OpCPX);
        R(0xEC, "CPX", AddressingMode.Absolute, Cpu6502.OpCPX);
        R(0xC0, "CPY", AddressingMode.Immediate, Cpu6502.OpCPY);
        R(0xC4, "CPY", AddressingMode.ZeroPage, Cpu6502.OpCPY);
        R(0xCC, "CPY", AddressingMode.Absolute, Cpu6502.OpCPY);
        R(0x24, "BIT", AddressingMode.ZeroPage, Cpu6502.OpBIT);
        R(0x2C, "BIT", AddressingMode.Absolute, Cpu6502.OpBIT);

        // ASL / LSR / ROL / ROR (memory forms + Accumulator form)
        M(0x0A, "ASL", AddressingMode.Accumulator, Cpu6502.OpASL);
        M(0x06, "ASL", AddressingMode.ZeroPage, Cpu6502.OpASL);
        M(0x16, "ASL", AddressingMode.ZeroPageX, Cpu6502.OpASL);
        M(0x0E, "ASL", AddressingMode.Absolute, Cpu6502.OpASL);
        M(0x1E, "ASL", AddressingMode.AbsoluteX, Cpu6502.OpASL);

        M(0x4A, "LSR", AddressingMode.Accumulator, Cpu6502.OpLSR);
        M(0x46, "LSR", AddressingMode.ZeroPage, Cpu6502.OpLSR);
        M(0x56, "LSR", AddressingMode.ZeroPageX, Cpu6502.OpLSR);
        M(0x4E, "LSR", AddressingMode.Absolute, Cpu6502.OpLSR);
        M(0x5E, "LSR", AddressingMode.AbsoluteX, Cpu6502.OpLSR);

        M(0x2A, "ROL", AddressingMode.Accumulator, Cpu6502.OpROL);
        M(0x26, "ROL", AddressingMode.ZeroPage, Cpu6502.OpROL);
        M(0x36, "ROL", AddressingMode.ZeroPageX, Cpu6502.OpROL);
        M(0x2E, "ROL", AddressingMode.Absolute, Cpu6502.OpROL);
        M(0x3E, "ROL", AddressingMode.AbsoluteX, Cpu6502.OpROL);

        M(0x6A, "ROR", AddressingMode.Accumulator, Cpu6502.OpROR);
        M(0x66, "ROR", AddressingMode.ZeroPage, Cpu6502.OpROR);
        M(0x76, "ROR", AddressingMode.ZeroPageX, Cpu6502.OpROR);
        M(0x6E, "ROR", AddressingMode.Absolute, Cpu6502.OpROR);
        M(0x7E, "ROR", AddressingMode.AbsoluteX, Cpu6502.OpROR);

        // INC / DEC (memory forms — INX/DEX/INY/DEY are Implied, added below)
        M(0xE6, "INC", AddressingMode.ZeroPage, Cpu6502.OpINC);
        M(0xF6, "INC", AddressingMode.ZeroPageX, Cpu6502.OpINC);
        M(0xEE, "INC", AddressingMode.Absolute, Cpu6502.OpINC);
        M(0xFE, "INC", AddressingMode.AbsoluteX, Cpu6502.OpINC);
        M(0xC6, "DEC", AddressingMode.ZeroPage, Cpu6502.OpDEC);
        M(0xD6, "DEC", AddressingMode.ZeroPageX, Cpu6502.OpDEC);
        M(0xCE, "DEC", AddressingMode.Absolute, Cpu6502.OpDEC);
        M(0xDE, "DEC", AddressingMode.AbsoluteX, Cpu6502.OpDEC);

        // Register/implied
        I(0xE8, "INX", Cpu6502.OpINX);
        I(0xCA, "DEX", Cpu6502.OpDEX);
        I(0xC8, "INY", Cpu6502.OpINY);
        I(0x88, "DEY", Cpu6502.OpDEY);
        I(0xAA, "TAX", Cpu6502.OpTAX);
        I(0x8A, "TXA", Cpu6502.OpTXA);
        I(0xA8, "TAY", Cpu6502.OpTAY);
        I(0x98, "TYA", Cpu6502.OpTYA);
        I(0xBA, "TSX", Cpu6502.OpTSX);
        I(0x9A, "TXS", Cpu6502.OpTXS);
        I(0x18, "CLC", Cpu6502.OpCLC);
        I(0x38, "SEC", Cpu6502.OpSEC);
        I(0x58, "CLI", Cpu6502.OpCLI);
        I(0x78, "SEI", Cpu6502.OpSEI);
        I(0xB8, "CLV", Cpu6502.OpCLV);
        I(0xD8, "CLD", Cpu6502.OpCLD);
        I(0xF8, "SED", Cpu6502.OpSED);
        I(0xEA, "NOP", Cpu6502.OpNOP);

        // Branches
        B(0x90, "BCC", Cpu6502.CondBCC);
        B(0xB0, "BCS", Cpu6502.CondBCS);
        B(0xF0, "BEQ", Cpu6502.CondBEQ);
        B(0xD0, "BNE", Cpu6502.CondBNE);
        B(0x30, "BMI", Cpu6502.CondBMI);
        B(0x10, "BPL", Cpu6502.CondBPL);
        B(0x50, "BVC", Cpu6502.CondBVC);
        B(0x70, "BVS", Cpu6502.CondBVS);

        AddIllegalOpcodes(R, W, M, I);
        return t;
    }

    /// <summary>
    /// The "stable" undocumented/illegal opcodes — combinations of two documented internal
    /// operations that behave consistently across NMOS 6502/2A03 chip revisions, and which
    /// real NES software and test ROMs (nestest, various demos) actually rely on. The eight
    /// analog-dependent, chip-revision-varying opcodes (ANE/XAA 0x8B, LXA 0xAB, SHA 0x93/0x9F,
    /// SHX 0x9E, SHY 0x9C, TAS 0x9B, LAS 0xBB) are intentionally left unimplemented — they
    /// have no single well-defined behavior to emulate and no known game or test ROM depends
    /// on them. JAM/KIL/HLT (12 opcodes) are handled directly in Cpu6502.FetchAndDecode, not
    /// through this table, since freezing the CPU isn't expressible as Read/Write/Modify.
    /// </summary>
    private static void AddIllegalOpcodes(
        Action<byte, string, AddressingMode, ReadOp> R,
        Action<byte, string, AddressingMode, WriteOp> W,
        Action<byte, string, AddressingMode, ModifyOp> M,
        Action<byte, string, ImpliedOp> I)
    {
        // NOP variants ("DOP"/"TOP" in older docs): read-and-discard through the stated
        // addressing mode. Reusing OpKind.Read here means the AbsoluteX page-crossing extra
        // cycle falls out for free, same as any other Read-kind opcode.
        I(0x1A, "NOP", Cpu6502.OpNOP);
        I(0x3A, "NOP", Cpu6502.OpNOP);
        I(0x5A, "NOP", Cpu6502.OpNOP);
        I(0x7A, "NOP", Cpu6502.OpNOP);
        I(0xDA, "NOP", Cpu6502.OpNOP);
        I(0xFA, "NOP", Cpu6502.OpNOP);

        R(0x80, "NOP", AddressingMode.Immediate, Cpu6502.OpNopRead);
        R(0x82, "NOP", AddressingMode.Immediate, Cpu6502.OpNopRead);
        R(0x89, "NOP", AddressingMode.Immediate, Cpu6502.OpNopRead);
        R(0xC2, "NOP", AddressingMode.Immediate, Cpu6502.OpNopRead);
        R(0xE2, "NOP", AddressingMode.Immediate, Cpu6502.OpNopRead);

        R(0x04, "NOP", AddressingMode.ZeroPage, Cpu6502.OpNopRead);
        R(0x44, "NOP", AddressingMode.ZeroPage, Cpu6502.OpNopRead);
        R(0x64, "NOP", AddressingMode.ZeroPage, Cpu6502.OpNopRead);

        R(0x14, "NOP", AddressingMode.ZeroPageX, Cpu6502.OpNopRead);
        R(0x34, "NOP", AddressingMode.ZeroPageX, Cpu6502.OpNopRead);
        R(0x54, "NOP", AddressingMode.ZeroPageX, Cpu6502.OpNopRead);
        R(0x74, "NOP", AddressingMode.ZeroPageX, Cpu6502.OpNopRead);
        R(0xD4, "NOP", AddressingMode.ZeroPageX, Cpu6502.OpNopRead);
        R(0xF4, "NOP", AddressingMode.ZeroPageX, Cpu6502.OpNopRead);

        R(0x0C, "NOP", AddressingMode.Absolute, Cpu6502.OpNopRead);

        R(0x1C, "NOP", AddressingMode.AbsoluteX, Cpu6502.OpNopRead);
        R(0x3C, "NOP", AddressingMode.AbsoluteX, Cpu6502.OpNopRead);
        R(0x5C, "NOP", AddressingMode.AbsoluteX, Cpu6502.OpNopRead);
        R(0x7C, "NOP", AddressingMode.AbsoluteX, Cpu6502.OpNopRead);
        R(0xDC, "NOP", AddressingMode.AbsoluteX, Cpu6502.OpNopRead);
        R(0xFC, "NOP", AddressingMode.AbsoluteX, Cpu6502.OpNopRead);

        // LAX (LDA+LDX)
        R(0xA7, "LAX", AddressingMode.ZeroPage, Cpu6502.OpLAX);
        R(0xB7, "LAX", AddressingMode.ZeroPageY, Cpu6502.OpLAX);
        R(0xAF, "LAX", AddressingMode.Absolute, Cpu6502.OpLAX);
        R(0xBF, "LAX", AddressingMode.AbsoluteY, Cpu6502.OpLAX);
        R(0xA3, "LAX", AddressingMode.IndirectX, Cpu6502.OpLAX);
        R(0xB3, "LAX", AddressingMode.IndirectY, Cpu6502.OpLAX);

        // SAX (store A & X) — note: zero-page,Y indexing, not ,X.
        W(0x87, "SAX", AddressingMode.ZeroPage, Cpu6502.OpSAX);
        W(0x97, "SAX", AddressingMode.ZeroPageY, Cpu6502.OpSAX);
        W(0x8F, "SAX", AddressingMode.Absolute, Cpu6502.OpSAX);
        W(0x83, "SAX", AddressingMode.IndirectX, Cpu6502.OpSAX);

        // DCP (DEC + CMP)
        M(0xC7, "DCP", AddressingMode.ZeroPage, Cpu6502.OpDCP);
        M(0xD7, "DCP", AddressingMode.ZeroPageX, Cpu6502.OpDCP);
        M(0xCF, "DCP", AddressingMode.Absolute, Cpu6502.OpDCP);
        M(0xDF, "DCP", AddressingMode.AbsoluteX, Cpu6502.OpDCP);
        M(0xDB, "DCP", AddressingMode.AbsoluteY, Cpu6502.OpDCP);
        M(0xC3, "DCP", AddressingMode.IndirectX, Cpu6502.OpDCP);
        M(0xD3, "DCP", AddressingMode.IndirectY, Cpu6502.OpDCP);

        // ISC/ISB (INC + SBC)
        M(0xE7, "ISC", AddressingMode.ZeroPage, Cpu6502.OpISC);
        M(0xF7, "ISC", AddressingMode.ZeroPageX, Cpu6502.OpISC);
        M(0xEF, "ISC", AddressingMode.Absolute, Cpu6502.OpISC);
        M(0xFF, "ISC", AddressingMode.AbsoluteX, Cpu6502.OpISC);
        M(0xFB, "ISC", AddressingMode.AbsoluteY, Cpu6502.OpISC);
        M(0xE3, "ISC", AddressingMode.IndirectX, Cpu6502.OpISC);
        M(0xF3, "ISC", AddressingMode.IndirectY, Cpu6502.OpISC);

        // SLO (ASL + ORA)
        M(0x07, "SLO", AddressingMode.ZeroPage, Cpu6502.OpSLO);
        M(0x17, "SLO", AddressingMode.ZeroPageX, Cpu6502.OpSLO);
        M(0x0F, "SLO", AddressingMode.Absolute, Cpu6502.OpSLO);
        M(0x1F, "SLO", AddressingMode.AbsoluteX, Cpu6502.OpSLO);
        M(0x1B, "SLO", AddressingMode.AbsoluteY, Cpu6502.OpSLO);
        M(0x03, "SLO", AddressingMode.IndirectX, Cpu6502.OpSLO);
        M(0x13, "SLO", AddressingMode.IndirectY, Cpu6502.OpSLO);

        // RLA (ROL + AND)
        M(0x27, "RLA", AddressingMode.ZeroPage, Cpu6502.OpRLA);
        M(0x37, "RLA", AddressingMode.ZeroPageX, Cpu6502.OpRLA);
        M(0x2F, "RLA", AddressingMode.Absolute, Cpu6502.OpRLA);
        M(0x3F, "RLA", AddressingMode.AbsoluteX, Cpu6502.OpRLA);
        M(0x3B, "RLA", AddressingMode.AbsoluteY, Cpu6502.OpRLA);
        M(0x23, "RLA", AddressingMode.IndirectX, Cpu6502.OpRLA);
        M(0x33, "RLA", AddressingMode.IndirectY, Cpu6502.OpRLA);

        // SRE (LSR + EOR)
        M(0x47, "SRE", AddressingMode.ZeroPage, Cpu6502.OpSRE);
        M(0x57, "SRE", AddressingMode.ZeroPageX, Cpu6502.OpSRE);
        M(0x4F, "SRE", AddressingMode.Absolute, Cpu6502.OpSRE);
        M(0x5F, "SRE", AddressingMode.AbsoluteX, Cpu6502.OpSRE);
        M(0x5B, "SRE", AddressingMode.AbsoluteY, Cpu6502.OpSRE);
        M(0x43, "SRE", AddressingMode.IndirectX, Cpu6502.OpSRE);
        M(0x53, "SRE", AddressingMode.IndirectY, Cpu6502.OpSRE);

        // RRA (ROR + ADC)
        M(0x67, "RRA", AddressingMode.ZeroPage, Cpu6502.OpRRA);
        M(0x77, "RRA", AddressingMode.ZeroPageX, Cpu6502.OpRRA);
        M(0x6F, "RRA", AddressingMode.Absolute, Cpu6502.OpRRA);
        M(0x7F, "RRA", AddressingMode.AbsoluteX, Cpu6502.OpRRA);
        M(0x7B, "RRA", AddressingMode.AbsoluteY, Cpu6502.OpRRA);
        M(0x63, "RRA", AddressingMode.IndirectX, Cpu6502.OpRRA);
        M(0x73, "RRA", AddressingMode.IndirectY, Cpu6502.OpRRA);

        // Immediate-mode oddities
        R(0xEB, "SBC", AddressingMode.Immediate, Cpu6502.OpSBC); // exact duplicate of 0xE9
        R(0x0B, "ANC", AddressingMode.Immediate, Cpu6502.OpANC);
        R(0x2B, "ANC", AddressingMode.Immediate, Cpu6502.OpANC);
        R(0x4B, "ALR", AddressingMode.Immediate, Cpu6502.OpALR);
        R(0x6B, "ARR", AddressingMode.Immediate, Cpu6502.OpARR);
        R(0xCB, "SBX", AddressingMode.Immediate, Cpu6502.OpSBX);
    }
}
