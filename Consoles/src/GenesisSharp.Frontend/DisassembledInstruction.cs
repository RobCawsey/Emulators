namespace GenesisSharp.Frontend;

/// <summary>One decoded instruction from either CPU disassembler. <see cref="BranchTarget"/> is
/// only populated for instructions whose destination is statically known from the opcode itself
/// (relative branches, absolute JP/JMP/CALL/JSR, fixed RST vectors) -- register-indirect jumps
/// (e.g. <c>JMP (A0)</c>, <c>JP (HL)</c>) leave it null since the real destination depends on
/// runtime register state this disassembler never reads.</summary>
public readonly record struct DisassembledInstruction(uint Address, string Text, int Length, uint? BranchTarget);
