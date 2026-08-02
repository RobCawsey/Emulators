using System.Security.Cryptography;

namespace GenesisSharp.Core;

/// <summary>Thrown by <see cref="GenesisConsole.LoadState"/> when the save state's ROM
/// fingerprint doesn't match the currently-loaded cartridge and the caller didn't opt into
/// loading anyway -- nothing is mutated before this is thrown, so the console is left exactly as
/// it was.</summary>
public sealed class SaveStateRomMismatchException : Exception
{
    public SaveStateRomMismatchException(string message) : base(message)
    {
    }
}

public sealed partial class GenesisConsole
{
    // "GSST" + format version -- bumped whenever a component's SaveState/LoadState layout
    // changes incompatibly, so LoadState can fail cleanly on an old/foreign file instead of
    // reading garbage into every field after the first mismatch.
    private static readonly byte[] SaveStateMagic = { (byte)'G', (byte)'S', (byte)'S', (byte)'T' };
    private const int SaveStateVersion = 2; // v2: added Sega32X (both SH-2s, 32X VDP, PWM) and the SH-2 cycle-debt fields

    /// <summary>A content fingerprint for a ROM image, used to warn on <see cref="LoadState"/>
    /// if a save state was made against a different cartridge than the one currently loaded.
    /// Not a security use -- just an identity check, so a fast, unsalted hash is fine.</summary>
    public static byte[] ComputeRomFingerprint(byte[] rom) => SHA256.HashData(rom);

    /// <summary>Writes a complete snapshot of every emulated component -- both main CPUs, the
    /// VDP (registers + VRAM/CRAM/VSRAM), both sound chips, all three controller ports, work RAM,
    /// the Z80's RAM/bank register/bus-arbitration state, the TMSS latch, and the 32X subsystem
    /// (both SH-2s, the 32X's own VDP overlay and PWM chip -- see <see cref="Sega32X.SaveState"/>)
    /// -- to <paramref name="stream"/>. Does not include <see cref="Vdp.FrameBuffer"/> (pure
    /// rendered output, reproduced fresh by the next scanline render) or the buffered-audio queue
    /// (a few stale queued samples from before the save aren't meaningful "game state" to
    /// restore). Safe to call between any two <see cref="RunFrame"/> calls, but not while one is
    /// in progress on another thread -- callers on a background emulation thread should perform
    /// this on that same thread, not concurrently from the UI thread.</summary>
    public void SaveState(Stream stream)
    {
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(SaveStateMagic);
        writer.Write(SaveStateVersion);
        writer.Write(ComputeRomFingerprint(Cartridge.Rom));

        Cpu.SaveState(writer);
        SoundCpu.SaveState(writer);
        Vdp.SaveState(writer);
        Ym2612.SaveState(writer);
        Psg.SaveState(writer);
        ControllerPort1.SaveState(writer);
        ControllerPort2.SaveState(writer);
        ExtPort.SaveState(writer);
        Sega32X.SaveState(writer);

        writer.Write(_workRam);
        writer.Write(_z80Ram);
        writer.Write(_z80BankRegister);
        writer.Write(_z80BusRequested);
        writer.Write(_z80Held);
        writer.Write(_crossBusAccessInProgress);
        writer.Write(_tmssRegister);

        writer.Write(_cpuCycleDebt);
        writer.Write(_z80CycleDebt);
        writer.Write(_msh2CycleDebt);
        writer.Write(_ssh2CycleDebt);
        writer.Write(_audioSampleDebt);
        writer.Write(AudioUnderrunCount);
        writer.Write(AudioClipCount);
    }

    /// <summary>Restores a snapshot written by <see cref="SaveState"/>. Reads and validates the
    /// header (magic, version, ROM fingerprint) before touching any component's state, so a
    /// rejected load -- an unrecognized file, an incompatible version, or (unless
    /// <paramref name="allowRomMismatch"/> is set) a fingerprint for a different cartridge --
    /// leaves the console completely untouched rather than partially overwritten. Also clears
    /// the buffered-audio queue on success, so playback doesn't pick up with a handful of stale
    /// samples generated before the load.</summary>
    public void LoadState(Stream stream, bool allowRomMismatch = false)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(SaveStateMagic.Length);
        if (!magic.AsSpan().SequenceEqual(SaveStateMagic))
        {
            throw new InvalidDataException("Not a GenesisSharp save state file.");
        }

        int version = reader.ReadInt32();
        if (version != SaveStateVersion)
        {
            throw new InvalidDataException($"Save state version {version} isn't compatible with this build (expects {SaveStateVersion}).");
        }

        byte[] savedFingerprint = reader.ReadBytes(32);
        if (!allowRomMismatch)
        {
            byte[] currentFingerprint = ComputeRomFingerprint(Cartridge.Rom);
            if (!savedFingerprint.AsSpan().SequenceEqual(currentFingerprint))
            {
                throw new SaveStateRomMismatchException("This save state was made with a different ROM than the one currently loaded.");
            }
        }

        Cpu.LoadState(reader);
        SoundCpu.LoadState(reader);
        Vdp.LoadState(reader);
        Ym2612.LoadState(reader);
        Psg.LoadState(reader);
        ControllerPort1.LoadState(reader);
        ControllerPort2.LoadState(reader);
        ExtPort.LoadState(reader);
        Sega32X.LoadState(reader);

        SaveStateIo.ReadExactly(reader, _workRam);
        SaveStateIo.ReadExactly(reader, _z80Ram);
        _z80BankRegister = reader.ReadUInt16();
        _z80BusRequested = reader.ReadBoolean();
        _z80Held = reader.ReadBoolean();
        _crossBusAccessInProgress = reader.ReadBoolean();
        SaveStateIo.ReadExactly(reader, _tmssRegister);

        _cpuCycleDebt = reader.ReadInt32();
        _z80CycleDebt = reader.ReadInt32();
        _msh2CycleDebt = reader.ReadInt32();
        _ssh2CycleDebt = reader.ReadInt32();
        _audioSampleDebt = reader.ReadDouble();
        AudioUnderrunCount = reader.ReadInt64();
        AudioClipCount = reader.ReadInt64();

        _audioBuffer.Clear();
    }
}
