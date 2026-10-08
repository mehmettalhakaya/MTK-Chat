using MTKChat.Contracts;

namespace MTKChat.Desktop;

internal enum CallConnectionState { Connecting, Connected, Reconnecting, Ended }

// A window/QA can observe lifecycle without opening the user's microphone.
internal interface ICallSession : IDisposable
{
    event Action<CallView>? Changed;
    event Action<string>? Failed;
    event Action<CallConnectionState>? ConnectionStateChanged;
    bool Muted { get; }
    bool SpeakerMuted { get; }
    CallConnectionState State { get; }
    IReadOnlyDictionary<Guid, string> Codes { get; }
    Task StartAsync();
    Task ToggleMuteAsync();
    void SetSpeakerMuted(bool muted);
}
internal interface ICallAudioFactory
{
    ICallCapture CreateCapture(Action<byte[], int> data, Action<Exception?> stopped);
    ICallPlayback CreatePlayback();
}
internal interface ICallCapture : IDisposable
{
    void Start();
    void Stop();
}
internal interface ICallPlayback : IDisposable
{
    TimeSpan BufferedDuration { get; }
    bool Muted { set; }
    void Start();
    void Clear();
    void Add(byte[] pcm);
}
internal sealed record CallSessionPolicy
{
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(3);
    internal TimeSpan RecoveryWindow { get; init; } = TimeSpan.FromSeconds(8);
    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    internal TimeSpan RosterInterval { get; init; } = TimeSpan.FromSeconds(1);
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    internal TimeSpan RingTimeout { get; init; } = TimeSpan.FromSeconds(60);
}
