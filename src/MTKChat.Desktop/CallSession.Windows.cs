using MTKChat.Cryptography;
using NAudio.Wave;

namespace MTKChat.Desktop;

internal sealed partial class CallSession
{
    internal CallSession(ChatApiClient api, Guid id, Guid user, DeviceIdentity device)
        : this(api, id, user, device, new WindowsCallAudioFactory()) { }
}
internal sealed class WindowsCallAudioFactory : ICallAudioFactory
{
    public ICallCapture CreateCapture(Action<byte[], int> data, Action<Exception?> stopped) => new Capture(data, stopped);
    public ICallPlayback CreatePlayback() => new Playback();
    private sealed class Capture : ICallCapture
    {
        private readonly WaveInEvent _input = new()
        { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        internal Capture(Action<byte[], int> data, Action<Exception?> stopped)
        {
            _input.DataAvailable += (_, e) => data(e.Buffer, e.BytesRecorded);
            _input.RecordingStopped += (_, e) => stopped(e.Exception);
        }
        public void Start() => _input.StartRecording();
        public void Stop() => _input.StopRecording();
        public void Dispose() => _input.Dispose();
    }
    private sealed class Playback : ICallPlayback
    {
        private readonly BufferedWaveProvider _buffer = new(new WaveFormat(16000, 16, 1), TimeSpan.FromMilliseconds(500))
        { DiscardOnBufferOverflow = true };
        private readonly WaveOutEvent _output = new() { DesiredLatency = 100 };
        public TimeSpan BufferedDuration => _buffer.BufferedDuration;
        public bool Muted { set => _output.Volume = value ? 0 : 1; }
        public void Start() { _output.Init(_buffer); _output.Play(); }
        public void Clear() => _buffer.ClearBuffer();
        public void Add(byte[] pcm) => _buffer.AddSamples(pcm, 0, pcm.Length);
        public void Dispose()
        {
            try { _output.Stop(); }
            finally { try { _output.Dispose(); } finally { _buffer.ClearBuffer(); } }
        }
    }
}
