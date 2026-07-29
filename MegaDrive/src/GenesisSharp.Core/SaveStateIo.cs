namespace GenesisSharp.Core;

/// <summary>Shared helper for the save-state readers/writers spread across this project's
/// components (<see cref="Vdp"/>, <see cref="Ym2612"/>, <see cref="Psg"/>, <see
/// cref="GenesisConsole"/>) -- <see cref="BinaryReader.Read(byte[], int, int)"/> is only
/// guaranteed to fill the whole buffer for a seekable, fully-buffered stream, and a save state
/// file being read incrementally doesn't promise that, so every fixed-size array read goes
/// through here instead of a single unchecked <c>Read</c> call.</summary>
internal static class SaveStateIo
{
    public static void ReadExactly(BinaryReader reader, byte[] destination)
    {
        int offset = 0;
        while (offset < destination.Length)
        {
            int read = reader.Read(destination, offset, destination.Length - offset);
            if (read == 0) throw new EndOfStreamException("Save state ended mid-array.");
            offset += read;
        }
    }
}
