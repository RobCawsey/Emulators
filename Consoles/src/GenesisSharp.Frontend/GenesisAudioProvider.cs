using GenesisSharp.Core;
using NAudio.Wave;

namespace GenesisSharp.Frontend;

/// <summary>Pulls stereo 16-bit PCM samples that <see cref="GenesisConsole.RunScanline"/>
/// already generated at cycle-accurate points in emulated time (see the doc comment on
/// <see cref="GenesisConsole.TryDequeueBufferedAudioSample"/>) — called from NAudio's own audio
/// thread, draining a queue the emulation thread fills, rather than generating samples live on
/// demand the way this used to work.</summary>
public sealed class GenesisAudioProvider : IWaveProvider
{
    private readonly GenesisConsole _console;

    public GenesisAudioProvider(GenesisConsole console)
    {
        _console = console;
        WaveFormat = new WaveFormat(GenesisConsole.AudioSampleRate, 16, 2);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int frameCount = count / 4; // 4 bytes per stereo Int16 frame
        int bytesWritten = 0;

        for (int i = 0; i < frameCount; i++)
        {
            // Silence (not a repeated/held sample) on underrun -- normal playback keeps this
            // queue topped up every host frame, so an empty buffer here means emulation is
            // genuinely paused or hasn't produced anything yet, and silence is the correct
            // sound for that rather than a stale or fabricated one.
            _console.TryDequeueBufferedAudioSample(out short left, out short right);
            int o = offset + i * 4;
            buffer[o] = (byte)(left & 0xFF);
            buffer[o + 1] = (byte)(left >> 8);
            buffer[o + 2] = (byte)(right & 0xFF);
            buffer[o + 3] = (byte)(right >> 8);
            bytesWritten += 4;
        }

        return bytesWritten;
    }
}
