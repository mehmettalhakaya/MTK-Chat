using NAudio.Wave;
using SoundTouch;

namespace MTKChat.Desktop;

// Tempo changes preserve vocal pitch; changing the output sample rate would raise the speaker's voice.
internal sealed class TempoSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _processor;
    private readonly float[] _input = new float[2048];
    private readonly object _gate = new();
    private bool _ended;
    public WaveFormat WaveFormat => _source.WaveFormat;
    internal TempoSampleProvider(ISampleProvider source, double tempo)
    {
        _source = source;
        _processor = new SoundTouchProcessor { SampleRate = source.WaveFormat.SampleRate, Channels = source.WaveFormat.Channels, Tempo = tempo };
    }
    internal void SetTempo(double value) { lock (_gate) _processor.Tempo = value; }
    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            var written = 0;
            while (written < count)
            {
                var received = _processor.ReceiveSamples(buffer.AsSpan(offset + written, count - written), (count - written) / WaveFormat.Channels);
                written += received * WaveFormat.Channels;
                if (written == count || _ended && received == 0) break;
                if (_ended) continue;
                var read = _source.Read(_input, 0, _input.Length);
                if (read == 0) { _processor.Flush(); _ended = true; }
                else _processor.PutSamples(_input.AsSpan(0, read), read / WaveFormat.Channels);
            }
            return written;
        }
    }
}
