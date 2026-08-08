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
    /// <summary>Per-sample multiplier applied to the held value while underrunning: about 12ms to
    /// fall to roughly a third at 44.1kHz. Chosen so the two failure modes it sits between both
    /// stay inaudible-ish. Snapping straight to zero puts a step discontinuity in the waveform,
    /// which is a click — and a run of underruns becomes a rapid click train, the "garbled" sound
    /// this is here to avoid. Holding the value flat instead turns a sustained underrun into a
    /// constant DC offset, or a buzz at the refill rate. Decaying does neither: a gap of a few
    /// samples is smoothed over, and a genuinely long one fades to real silence rather than
    /// droning.</summary>
    private const double UnderrunDecayPerSample = 0.99991;

    private readonly GenesisConsole _console;

    private double _lastLeft;
    private double _lastRight;

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
            // An empty queue means the emulation thread hasn't produced this sample yet -- it is
            // paused, or it fell behind real time. Neither is a reason to emit a discontinuity, so
            // coast from the last real sample instead of cutting to zero; see the decay constant's
            // own remarks. GenesisConsole counts these in AudioUnderrunCount either way, so the
            // condition stays diagnosable rather than being smoothed out of existence.
            if (_console.TryDequeueBufferedAudioSample(out short left, out short right))
            {
                _lastLeft = left;
                _lastRight = right;
            }
            else
            {
                _lastLeft *= UnderrunDecayPerSample;
                _lastRight *= UnderrunDecayPerSample;
                left = (short)_lastLeft;
                right = (short)_lastRight;
            }

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
