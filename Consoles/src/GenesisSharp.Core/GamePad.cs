namespace GenesisSharp.Core;

/// <summary>A Genesis controller's button state, plus the TH-multiplexed read protocol that
/// exposes it over the data lines. The base 3-button protocol is widely and consistently
/// documented (reproduced near-identically across most Genesis homebrew references): the
/// console drives TH itself (an output), and the pad answers with a different half of its
/// buttons depending on which level TH is currently at.
///
/// <see cref="SixButton"/> layers the 6-button pad's extended sequence on top: six rapid TH
/// transitions in a row (faster than any real game's normal per-frame D-pad poll — see <see
/// cref="ControllerPort"/> for how that's told apart from a genuine detection attempt) walk
/// through six steps, the 4th and 5th of which are where a 6-button pad diverges from a
/// 3-button one (forcing D0-D3 to all-0, then exposing X/Y/Z/Mode there) before the 6th step
/// (all-1) hands back to the normal steps. This exact six-step table is also widely and
/// consistently cited; a plain 3-button pad (<see cref="SixButton"/> false) simply never
/// diverges from the normal two-step behavior no matter what step it's asked for.</summary>
public sealed class GamePad
{
    /// <summary>False models nothing plugged into the port at all: all data lines simply read
    /// high (no pull-down from any pad), including the TH=0 lines a 3-button pad would
    /// otherwise ground — which is exactly the signal software uses to detect a pad's
    /// presence.</summary>
    public bool Connected { get; set; } = true;

    /// <summary>When true, answers the 6-button detection sequence's steps 3-5 (see the type
    /// remarks) instead of just repeating the normal 2-step behavior a 3-button pad would.</summary>
    public bool SixButton { get; set; }

    public bool Up, Down, Left, Right, A, B, C, Start;
    public bool X, Y, Z, Mode;

    /// <summary>The 6 data lines (D0-D5), active-low (a pressed button pulls its line to 0).
    /// TH (D6) and the unused D7 line aren't included here -- <see cref="ControllerPort"/>
    /// layers those on separately. <paramref name="sequenceStep"/> is <see
    /// cref="ControllerPort"/>'s count of rapid TH transitions (0-5, wrapping); only steps 3-5
    /// mean anything here, and only when <see cref="SixButton"/> is set.</summary>
    public byte ReadDataLines(bool th, int sequenceStep = 0)
    {
        if (!Connected)
        {
            return 0x3F;
        }

        if (SixButton)
        {
            switch (sequenceStep)
            {
                case 3: // 2nd TH=0 of the rapid sequence: direction bits forced low
                    return (byte)((A ? 0 : 0x10) | (Start ? 0 : 0x20));
                case 4: // 2nd TH=1: the extra buttons appear where the directions normally would
                    return (byte)(
                        (X ? 0 : 0x01) | (Y ? 0 : 0x02) | (Z ? 0 : 0x04) | (Mode ? 0 : 0x08) |
                        (B ? 0 : 0x10) | (C ? 0 : 0x20));
                case 5: // 3rd TH=0: direction bits forced high -- the "end of sequence" marker
                    return (byte)(0x0F | (A ? 0 : 0x10) | (Start ? 0 : 0x20));
            }
        }

        if (th)
        {
            return (byte)(
                (Up ? 0 : 0x01) | (Down ? 0 : 0x02) | (Left ? 0 : 0x04) | (Right ? 0 : 0x08) |
                (B ? 0 : 0x10) | (C ? 0 : 0x20));
        }

        // D2/D3 are forced low regardless of Left/Right when TH=0 -- this is the pad-presence
        // signal: an empty port floats high there instead (see Connected above).
        return (byte)(
            (Up ? 0 : 0x01) | (Down ? 0 : 0x02) |
            (A ? 0 : 0x10) | (Start ? 0 : 0x20));
    }

    /// <summary>Included in a save state mainly for consistency (this is live input state, not
    /// emulated game state) -- restoring it just means a state saved mid-button-press comes
    /// back with that press still held, which self-corrects on the next real key event either
    /// way.</summary>
    public void SaveState(BinaryWriter writer)
    {
        writer.Write(Connected);
        writer.Write(SixButton);
        writer.Write(Up); writer.Write(Down); writer.Write(Left); writer.Write(Right);
        writer.Write(A); writer.Write(B); writer.Write(C); writer.Write(Start);
        writer.Write(X); writer.Write(Y); writer.Write(Z); writer.Write(Mode);
    }

    public void LoadState(BinaryReader reader)
    {
        Connected = reader.ReadBoolean();
        SixButton = reader.ReadBoolean();
        Up = reader.ReadBoolean(); Down = reader.ReadBoolean(); Left = reader.ReadBoolean(); Right = reader.ReadBoolean();
        A = reader.ReadBoolean(); B = reader.ReadBoolean(); C = reader.ReadBoolean(); Start = reader.ReadBoolean();
        X = reader.ReadBoolean(); Y = reader.ReadBoolean(); Z = reader.ReadBoolean(); Mode = reader.ReadBoolean();
    }
}
