using NAudio.Wave;

namespace NesSharp.Frontend;

/// <summary>
/// Streams Apu2A03's raw sample queue to the default audio device via NAudio. NesSharp.Core
/// deliberately outputs unfiltered samples (the mixer's DC-biased 0..~1 signal, matching what
/// real hardware's mixer produces before its own output capacitor removes the bias) — that
/// filtering is a presentation concern, not something the emulation core should own, so it
/// lives here: a minimal one-pole DC-blocking high-pass filter, just enough to center the
/// waveform around 0 for comfortable listening.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const float DcBlockPole = 0.995f;

    private readonly BufferedWaveProvider _buffer;
    private readonly WaveOutEvent _output;
    private float _prevInput;
    private float _prevOutput;

    public AudioPlayer()
    {
        WaveFormat format = WaveFormat.CreateIeeeFloatWaveFormat(44100, channels: 1);
        _buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
            DiscardOnBufferOverflow = true,
        };
        _output = new WaveOutEvent();
        _output.Init(_buffer);
        _output.Play();
    }

    public void Enqueue(Queue<float> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var bytes = new byte[samples.Count * 4];
        int offset = 0;
        while (samples.Count > 0)
        {
            float raw = samples.Dequeue();
            float filtered = raw - _prevInput + DcBlockPole * _prevOutput;
            _prevInput = raw;
            _prevOutput = filtered;

            Span<byte> destination = bytes.AsSpan(offset, 4);
            BitConverter.TryWriteBytes(destination, filtered);
            offset += 4;
        }
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose() => _output.Dispose();
}
