namespace GenesisSharp.Frontend;

/// <summary>Turns a contiguous run of <see cref="DisassembledInstruction"/>s into display
/// lines with symbolic labels -- shared between the 68000 and Z80 listings since the labeling
/// rule is identical for both: only addresses that are both (a) a branch/jump/call target
/// somewhere in the same listing and (b) themselves an instruction's address within that same
/// listing get a label. A target that falls outside the visible window has nothing to point a
/// label at, so it's left as a plain address in that instruction's text instead.</summary>
public static class DisassemblyLabeler
{
    public static IReadOnlyList<string> FormatWithLabels(
        IReadOnlyList<DisassembledInstruction> instructions, uint currentPc, Func<uint, string> formatAddress)
    {
        var addressesInListing = new HashSet<uint>();
        foreach (var instruction in instructions)
        {
            addressesInListing.Add(instruction.Address);
        }

        var labelTargets = new HashSet<uint>();
        foreach (var instruction in instructions)
        {
            if (instruction.BranchTarget.HasValue && addressesInListing.Contains(instruction.BranchTarget.Value))
            {
                labelTargets.Add(instruction.BranchTarget.Value);
            }
        }

        var lines = new List<string>(instructions.Count);
        foreach (var instruction in instructions)
        {
            string marker = instruction.Address == currentPc ? ">" : " ";
            string label = labelTargets.Contains(instruction.Address) ? $"L{formatAddress(instruction.Address)}:" : string.Empty;
            string branchSuffix = instruction.BranchTarget.HasValue && labelTargets.Contains(instruction.BranchTarget.Value)
                ? $"  ; -> L{formatAddress(instruction.BranchTarget.Value)}"
                : string.Empty;
            lines.Add($"{marker} {formatAddress(instruction.Address)}  {label,-9}{instruction.Text}{branchSuffix}");
        }

        return lines;
    }
}
