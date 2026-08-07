using GenesisSharp.Core;

namespace GenesisSharp.DebugTools;

/// <summary>Runs a ROM headlessly and reports the 32X display state once per frame, printing only
/// when something changes.
///
/// The 32X frame buffer is not a plain bitmap: the low 512 bytes of each bank hold a per-scanline
/// table of word offsets that the game writes itself, and nothing renders from a bank whose table
/// is empty. That makes "how many scanlines can this bank actually resolve?" the single most
/// diagnostic number available about 32X output, and it is invisible from a screenshot — a bank
/// with a full image but no line table looks exactly like a bank with nothing in it.
///
/// This exists because stepping a real title frame by frame in the debug window and peeking one
/// address at a time is a slow way to answer questions that are really about a *sequence* of
/// frames. It found the FM-ownership bug (see ARCHITECTURE.md §4a): bank 1's table was fully
/// populated, bank 0's was empty forever, both banks held pixel data, and FS flipped every frame —
/// which is a 30Hz flicker, stated in four numbers.</summary>
internal static class FrameBufferProbeCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: probe <rom> [frames]");
            return 2;
        }

        string romPath = args[0];
        int frames = 900;
        if (args.Length > 1 && !int.TryParse(args[1], out frames))
        {
            Console.Error.WriteLine($"Not a frame count: {args[1]}");
            return 2;
        }

        var console = new GenesisConsole(Cartridge.LoadFromBin(File.ReadAllBytes(romPath)));
        console.Reset(); // matches MainForm.StartConsole -- without it the 68000 starts from a bad vector
        var sega32X = console.Sega32X;

        Console.WriteLine($"; {Path.GetFileName(romPath)}, {frames} frames");
        Console.WriteLine("; lines0/lines1 = scanlines each bank's line table can actually resolve (of 224)");
        Console.WriteLine();

        string previous = "";
        int previousFs = sega32X.VdpRegs[5] & 0x1;
        int flipsSincePrint = 0;

        for (int frame = 1; frame <= frames; frame++)
        {
            console.RunFrame();

            int fs = sega32X.VdpRegs[5] & 0x1;
            if (fs != previousFs)
            {
                flipsSincePrint++;
                previousFs = fs;
            }

            // FS is deliberately NOT part of the change key: a page-flipping title toggles it every
            // single frame, which would make every frame a "change" and bury the signal. It's
            // reported as a flip count instead, which is the form the question actually takes --
            // "is this title flipping, and how often" rather than "what is it this instant".
            //
            // Mx (display mode) and FM (register ownership) *are* in the key, because a change in
            // either reframes everything else: Mx=0 means the 32X layer is off entirely, and FM
            // decides which CPU's register writes are being accepted at all.
            int lines0 = ResolvableLines(sega32X.FrameBuffer[0]);
            int lines1 = ResolvableLines(sega32X.FrameBuffer[1]);
            string state = $"Mx={sega32X.VdpRegs[0] & 0x3} FM={((sega32X.Regs[0] & 0x8000) != 0 ? "sh2" : "68k")} " +
                           $"lines0={lines0,3} lines1={lines1,3}";

            bool changed = state != previous;
            if (changed || frame == frames)
            {
                Console.WriteLine($"frame {frame,5}  {state}  FS={fs} (+{flipsSincePrint} flips)" +
                                  $"{(changed ? "   <-- change" : "")}");
                previous = state;
                flipsSincePrint = 0;
            }
        }

        return 0;
    }

    /// <summary>How many of the 224 visible scanlines have a non-zero line-table entry. A bank the
    /// game has drawn into gives 224; one it has never set up gives 0, and renders as garbage or
    /// nothing regardless of how much pixel data it holds.</summary>
    private static int ResolvableLines(byte[] bank)
    {
        int resolvable = 0;
        for (int line = 0; line < 224; line++)
        {
            if (((bank[line * 2] << 8) | bank[line * 2 + 1]) != 0)
            {
                resolvable++;
            }
        }

        return resolvable;
    }
}
