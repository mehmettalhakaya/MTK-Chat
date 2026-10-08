using System.Security.Cryptography;
using NAudio.Wave;

namespace MTKChat.Desktop;

internal sealed class VoicePlayback : IDisposable
{
    private byte[]? _audio;
    private MemoryStream? _stream;
    private WaveFileReader? _reader;
    private WaveOutEvent? _output;
    private TempoSampleProvider? _tempo;
    private double _speed = 1;
    private static VoicePlayback? _active;
    internal double Speed => _speed;
    internal TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    internal TimeSpan Duration => _reader?.TotalTime ?? TimeSpan.Zero;
    internal void CycleSpeed()
    {
        _speed = _speed == 1 ? 1.5 : _speed == 1.5 ? 2 : 1;
        _tempo?.SetTempo(_speed);
    }
    internal static void StopCurrent() => _active?.Stop();
    private bool _disposed;
    private readonly Control _owner;
    private readonly Action<bool> _onState;

    internal VoicePlayback(Control owner, byte[] audio, Action<bool> onState)
    {
        _owner = owner;
        _audio = audio;
        _onState = onState;
    }

    internal void Toggle()
    {
        if (_disposed) return;
        if (_output is { PlaybackState: PlaybackState.Playing }) { _output.Pause(); _onState(false); return; }
        if (_active != this) _active?.Stop();
        _active = this;
        if (_output is { PlaybackState: PlaybackState.Paused }) { _output.Play(); _onState(true); return; }
        Stop();
        try
        {
            _stream = new MemoryStream(_audio!, writable: false);
            _reader = new WaveFileReader(_stream);
            if (_reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || _reader.WaveFormat.SampleRate != 16000 ||
                _reader.WaveFormat.Channels != 1 || _reader.WaveFormat.BitsPerSample != 16 ||
                _reader.TotalTime > VoiceRecorder.MaxDuration.Add(TimeSpan.FromSeconds(1)))
                throw new InvalidDataException("Ses dosyasının biçimi desteklenmiyor.");
            var output = new WaveOutEvent();
            _output = output;
            output.PlaybackStopped += (_, _) =>
            {
                if (!_owner.IsDisposed && _owner.IsHandleCreated)
                {
                    try { _owner.BeginInvoke(() => { if (!_disposed && ReferenceEquals(_output, output)) Stop(); }); }
                    catch (InvalidOperationException) { /* Owner can close after the handle check. */ }
                }
            };
            _tempo = new TempoSampleProvider(_reader.ToSampleProvider(), _speed);
            _output.Init(_tempo);
            _output.Play();
            _active = this;
            _onState(true);
        }
        catch
        {
            Stop();
            throw;
        }
    }

    internal void Stop()
    {
        if (_output is { } output)
        {
            _output = null;
            output.Stop();
            output.Dispose();
        }
        _reader?.Dispose();
        _reader = null;
        _stream?.Dispose();
        _stream = null;
        _tempo = null;
        if (_active == this) _active = null;
        if (!_disposed) _onState(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        if (_audio is not null) CryptographicOperations.ZeroMemory(_audio);
        _audio = null;
    }
}
