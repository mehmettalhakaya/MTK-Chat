using System.Security.Cryptography;
using NAudio.Wave;

namespace MTKChat.Desktop;

// 16 kHz mono PCM: a 45-second WAV is about 1.4 MB before encryption.
internal sealed class VoiceRecorder : IDisposable
{
    internal static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(45);
    private readonly WaveInEvent _input;
    private readonly MemoryStream _buffer = new();
    private readonly WaveFileWriter _writer;
    private readonly TaskCompletionSource<byte[]> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _gate = new();
    private bool _finished;
    private bool _disposed;
    private int _capturedBytes;
    internal TimeSpan Duration => TimeSpan.FromSeconds(_capturedBytes / 32000d);
    internal DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    internal VoiceRecorder()
    {
        _input = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        _writer = new WaveFileWriter(_buffer, _input.WaveFormat);
        _input.DataAvailable += (_, eventArgs) =>
        {
            bool reachedLimit;
            lock (_gate)
            {
                if (_finished) return;
                // Bound captured data even while the UI thread is occupied by a modal dialog.
                var remaining = 16000 * 2 * 45 - _capturedBytes;
                var count = Math.Min(remaining, eventArgs.BytesRecorded);
                if (count <= 0) return;
                _writer.Write(eventArgs.Buffer, 0, count);
                _capturedBytes += count;
                reachedLimit = _capturedBytes >= 16000 * 2 * 45;
            }
            // Stop the microphone independently of the UI timer, including while a dialog is open.
            if (reachedLimit) _input.StopRecording();
        };
        _input.RecordingStopped += (_, eventArgs) =>
        {
            lock (_gate)
            {
                if (_finished) return;
                _finished = true;
                try
                {
                    _writer.Dispose(); // Finalizes WAV length fields.
                    if (eventArgs.Exception is { } error) _completion.TrySetException(error);
                    else _completion.TrySetResult(_buffer.ToArray());
                }
                catch (Exception error) { _completion.TrySetException(error); }
            }
        };
        try { _input.StartRecording(); }
        catch { _input.Dispose(); _writer.Dispose(); _buffer.Dispose(); throw; }
    }

    internal async Task<byte[]> StopAsync()
    {
        bool shouldStop;
        lock (_gate) shouldStop = !_finished;
        if (shouldStop) _input.StopRecording();
        return await _completion.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { if (!_finished) _input.StopRecording(); } catch { /* Device can disappear mid-recording. */ }
        _input.Dispose();
        lock (_gate)
        {
            if (!_finished)
            {
                try { _writer.Dispose(); }
                finally { _completion.TrySetCanceled(); }
            }
            _finished = true;
            // MemoryStream.Dispose alone does not erase the recorded plaintext.
            if (_buffer.TryGetBuffer(out var recorded)) CryptographicOperations.ZeroMemory(recorded.AsSpan());
        }
        _buffer.Dispose();
    }
}
