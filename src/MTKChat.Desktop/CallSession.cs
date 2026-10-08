using System.Net;
using System.Security.Cryptography;
using System.Threading.Channels;
using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal sealed partial class CallSession : ICallSession
{
    private readonly ChatApiClient _api;
    private readonly Guid _id, _user;
    private readonly CallIdentity _identity;
    private readonly ICallAudioFactory _audio;
    private readonly CallSessionPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly object _gate = new();
    private readonly object _captureGate = new();
    private readonly SemaphoreSlim _muteCommand = new(1, 1);
    private readonly Dictionary<Guid, PeerAudio> _peers = new();
    private readonly HashSet<(Guid User, Guid Session)> _retiredPeerSessions = [];
    private readonly Channel<IReadOnlyList<CallFrame>> _outgoing = Channel.CreateBounded<IReadOnlyList<CallFrame>>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
    private ICallCapture? _input;
    private CancellationTokenSource? _voiceRequest;
    private long _captureEpoch;
    private Task? _startTask, _workers;
    private bool _disposed, _ending, _muted, _speakerMuted, _everConnected;
    private DateTimeOffset? _sendFailure, _receiveFailure;
    private CallConnectionState _state = CallConnectionState.Connecting;
    public event Action<CallView>? Changed;
    public event Action<string>? Failed;
    public event Action<CallConnectionState>? ConnectionStateChanged;
    public bool Muted { get { lock (_gate) return _muted; } }
    public bool SpeakerMuted { get { lock (_gate) return _speakerMuted; } }
    public CallConnectionState State { get { lock (_gate) return _state; } }
    public IReadOnlyDictionary<Guid, string> Codes
    { get { lock (_gate) return _peers.ToDictionary(p => p.Key, p => p.Value.Cipher.VerificationCode); } }

    private sealed class PeerAudio(CallPeer peer, CallCipher cipher, ICallPlayback output) : IDisposable
    {
        internal CallPeer Peer = peer;
        internal CallCipher Cipher = cipher;
        internal ICallPlayback Output = output;
        public void Dispose() { try { Output.Dispose(); } finally { Cipher.Dispose(); } }
    }

    internal CallSession(ChatApiClient api, Guid id, Guid user, DeviceIdentity device,
        ICallAudioFactory audio, CallSessionPolicy? policy = null, TimeProvider? clock = null)
    {
        _api = api; _id = id; _user = user; _audio = audio;
        _policy = policy ?? new(); _clock = clock ?? TimeProvider.System;
        _token = _stop.Token;
        _identity = new CallIdentity(id, user, device.SigningKey);
    }

    public Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A fake/cache/synchronously completed HTTP handler can continue all
            // the way to native Start. Schedule it rather than executing that
            // code while this outer lock (or the UI thread) is still held.
            return _startTask ??= Task.Run(StartCoreAsync);
        }
    }

    private async Task StartCoreAsync()
    {
        try
        {
            _token.ThrowIfCancellationRequested();
            var view = await JoinWithRecoveryAsync().ConfigureAwait(false);
            await ReconcileStartupAsync(view).ConfigureAwait(false);
            _token.ThrowIfCancellationRequested();
            var capture = _audio.CreateCapture(CaptureData, error =>
            { if (error is not null) FailFromAudio("Mikrofon bağlantısı kesildi."); });
            bool rejectCapture;
            lock (_gate)
            {
                rejectCapture = _disposed;
                if (!rejectCapture) _input = capture;
            }
            // Dispose sets _disposed before cancelling its token. Do not rely
            // on that cancellation already being visible in this narrow race.
            if (rejectCapture) { DisposeQuietly(capture); throw new OperationCanceledException(_token); }
            // Native start/stop may wait for a callback, so neither waits under
            // the cipher lock. Dispose cancels an in-flight Join before capture.
            try
            {
                lock (_captureGate) { _token.ThrowIfCancellationRequested(); capture.Start(); }
            }
            catch
            {
                bool ownsCapture;
                lock (_gate)
                {
                    ownsCapture = _input == capture;
                    if (ownsCapture) _input = null;
                }
                // Dispose may already have detached and claimed this capture.
                // Exactly one owner performs native teardown, including failure.
                if (ownsCapture) DisposeQuietly(capture);
                throw;
            }
            _token.ThrowIfCancellationRequested();
            _workers = Task.WhenAll(GuardAsync(SendLoopAsync), GuardAsync(ReceiveLoopAsync));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { throw; }
        catch (Exception ex) { End(FriendlyFailure(ex)); throw; }
    }

    private async Task<CallView> JoinWithRecoveryAsync()
    {
        var began = _clock.GetUtcNow();
        while (true)
        {
            try { return await RequestAsync(ct => _api.JoinCallAsync(_id, _identity.Join, ct)).ConfigureAwait(false); }
            catch (ChatApiException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
            {
                // A legacy relay is not idempotent. Only this exact signed
                // participation can turn its "already joined" into success.
                var confirmed = await RequestAsync(ct => _api.GetCallAsync(_id, ct)).ConfigureAwait(false);
                if (confirmed.Peers.Any(p => p.User.Id == _user && p.Identity == _identity.Join)) return confirmed;
                throw;
            }
            catch (Exception ex) when (IsTransient(ex) && !_token.IsCancellationRequested)
            {
                if (_clock.GetUtcNow() - began >= _policy.RecoveryWindow) throw;
                SetState(CallConnectionState.Reconnecting);
                // A lost POST response may already have committed. Confirm the
                // exact signed session before retrying, including on old relays.
                while (true)
                {
                    try
                    {
                        var view = await RequestAsync(ct => _api.GetCallAsync(_id, ct)).ConfigureAwait(false);
                        if (view.Peers.Any(p => p.User.Id == _user && p.Identity == _identity.Join)) return view;
                        break; // A successful roster proves the POST was not registered.
                    }
                    catch (Exception probe) when (IsTransient(probe) && !_token.IsCancellationRequested)
                    {
                        if (_clock.GetUtcNow() - began >= _policy.RecoveryWindow) throw;
                        // Never replay the Join while its outcome is still unknown.
                        await Task.Delay(_policy.RetryDelay, _clock, _token).ConfigureAwait(false);
                    }
                }
                if (_clock.GetUtcNow() - began >= _policy.RecoveryWindow) throw;
                await Task.Delay(_policy.RetryDelay, _clock, _token).ConfigureAwait(false);
            }
        }
    }

    private async Task ReconcileStartupAsync(CallView view)
    {
        var began = _clock.GetUtcNow();
        while (true)
        {
            try { await ReconcileAsync(view).ConfigureAwait(false); return; }
            catch (Exception ex) when (IsTransient(ex) && !_token.IsCancellationRequested)
            {
                if (_clock.GetUtcNow() - began >= _policy.RecoveryWindow) throw;
                SetState(CallConnectionState.Reconnecting);
                await Task.Delay(_policy.RetryDelay, _clock, _token).ConfigureAwait(false);
                // Only read/key lookup is repeated, never the accepted Join POST.
            }
        }
    }

    private void CaptureData(byte[] data, int length)
    {
        try
        {
            lock (_gate)
            {
                if (_disposed || _muted || _peers.Count == 0 || length < 2 || length > data.Length) return;
                for (var offset = 0; offset < length; offset += 3200)
                {
                    var count = Math.Min(3200, length - offset);
                    if (count % 2 != 0) continue;
                    _outgoing.Writer.TryWrite(_peers.Values.Select(p => p.Cipher.Encrypt(data.AsSpan(offset, count))).ToArray());
                }
            }
        }
        catch (Exception ex) when (ex is CryptographicException or OverflowException or ObjectDisposedException)
        { FailFromAudio("Ses şifrelemesi sürdürülemedi."); }
    }

    private void FailFromAudio(string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _muted = true;
            while (_outgoing.Reader.TryRead(out _)) { }
        }
        // Native capture shutdown must not wait for the very callback executing
        // it. Move teardown off the driver's callback thread after closing input.
        ThreadPool.QueueUserWorkItem(_ => End(message));
    }

    private async Task GuardAsync(Func<Task> loop)
    {
        try { await loop().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { End(FriendlyFailure(ex)); }
    }

    private async Task SendLoopAsync()
    {
        while (await _outgoing.Reader.WaitToReadAsync(_token).ConfigureAwait(false))
        {
            var batch = new List<CallFrame>();
            long epoch;
            lock (_gate)
            {
                if (_disposed) return;
                epoch = _captureEpoch;
                while (batch.Count <= 15 && _outgoing.Reader.TryRead(out var frames))
                    if (!_muted) batch.AddRange(frames);
            }
            if (batch.Count == 0) continue;
            using var voiceRequest = new CancellationTokenSource();
            lock (_gate)
            {
                if (_disposed || _muted || epoch != _captureEpoch) continue;
                _voiceRequest = voiceRequest;
            }
            try
            {
                // Never replay an uncertain voice POST: future batches use new
                // GCM sequence numbers, while delayed audio is discarded.
                await RequestAsync(ct => _api.SendCallFramesAsync(_id, batch, ct), voiceRequest.Token).ConfigureAwait(false);
                if (!ObsoleteVoiceBatch(epoch)) MarkHealthy(sending: true);
            }
            catch (Exception ex) when (IsTransient(ex) && !_token.IsCancellationRequested)
            {
                if (ObsoleteVoiceBatch(epoch)) continue;
                MarkFailure(sending: true);
                lock (_gate) while (_outgoing.Reader.TryRead(out _)) { }
                await Task.Delay(_policy.RetryDelay, _clock, _token).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) { if (ReferenceEquals(_voiceRequest, voiceRequest)) _voiceRequest = null; }
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var lastRoster = DateTimeOffset.MinValue;
        while (!_token.IsCancellationRequested)
        {
            try
            {
                CheckRecoveryDeadline();
                if (_clock.GetUtcNow() - lastRoster >= _policy.RosterInterval)
                {
                    var view = await RequestAsync(ct => _api.GetCallAsync(_id, ct)).ConfigureAwait(false);
                    if (!_everConnected && _clock.GetUtcNow() - view.CreatedAt >= _policy.RingTimeout)
                        throw new InvalidOperationException("Arama yanıtlanmadı.");
                    await ReconcileAsync(view).ConfigureAwait(false);
                    lastRoster = _clock.GetUtcNow();
                }
                var frames = await RequestAsync(ct => _api.GetCallFramesAsync(_id, ct)).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed) return;
                    foreach (var frame in frames)
                    {
                        if (!_peers.TryGetValue(frame.SenderId, out var peer) || peer.Peer.Muted) continue;
                        byte[]? pcm = null;
                        try
                        {
                            pcm = peer.Cipher.Decrypt(frame);
                            if (peer.Output.BufferedDuration > TimeSpan.FromMilliseconds(300)) peer.Output.Clear();
                            if (!_speakerMuted) peer.Output.Add(pcm);
                        }
                        catch (Exception ex) when (ex is CryptographicException or FormatException)
                        { /* Forged/replayed packets never enter an audio buffer. */ }
                        finally { if (pcm is not null) CryptographicOperations.ZeroMemory(pcm); }
                    }
                }
                MarkHealthy(sending: false);
                await Task.Delay(_policy.PollInterval, _clock, _token).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex) && !_token.IsCancellationRequested)
            {
                MarkFailure(sending: false);
                await Task.Delay(_policy.RetryDelay, _clock, _token).ConfigureAwait(false);
            }
        }
    }

    private async Task ReconcileAsync(CallView view)
    {
        PeerAudio[] removed;
        lock (_gate)
        {
            if (_disposed) return;
            removed = _peers.Values.Where(p => !view.Peers.Any(x => x.User.Id == p.Peer.User.Id &&
                x.Identity == p.Peer.Identity)).ToArray();
            foreach (var peer in removed)
            {
                _peers.Remove(peer.Peer.User.Id);
                _retiredPeerSessions.Add((peer.Peer.User.Id, peer.Peer.Identity.SessionId));
            }
        }
        foreach (var peer in removed) DisposeQuietly(peer);
        foreach (var peer in view.Peers.Where(p => p.User.Id != _user))
        {
            lock (_gate)
            {
                if (_disposed) return;
                // Recreating a cipher for a previously omitted signed session
                // would reset its send sequence under the same GCM key/nonce.
                // Fail closed rather than resurrect any retired participation.
                if (_retiredPeerSessions.Contains((peer.User.Id, peer.Identity.SessionId)))
                    throw new CryptographicException("Sona ermiş arama kimliği yeniden kullanılamaz.");
                if (_peers.TryGetValue(peer.User.Id, out var known))
                {
                    if (peer.Muted && !known.Peer.Muted) known.Output.Clear();
                    known.Peer = peer;
                    continue;
                }
            }
            var device = await RequestAsync(ct => _api.TryGetDeviceAsync(peer.User.Id, ct)).ConfigureAwait(false)
                ?? throw new CryptographicException("Katılımcının cihaz anahtarı bulunamadı.");
            CallCipher cipher;
            lock (_gate)
            {
                if (_disposed) return;
                cipher = _identity.Connect(peer.User.Id, peer.Identity, device.SigningPublicKey);
            }
            ICallPlayback? output = null;
            PeerAudio? audio = null;
            try
            {
                output = _audio.CreatePlayback(); output.Start();
                audio = new PeerAudio(peer, cipher, output);
                var abandoned = false;
                lock (_gate)
                {
                    abandoned = _disposed;
                    if (!abandoned)
                    {
                        output.Muted = _speakerMuted;
                        _peers.Add(peer.User.Id, audio); _everConnected = true;
                    }
                }
                if (abandoned) { DisposeQuietly(audio); return; }
            }
            catch { if (audio is not null) DisposeQuietly(audio); else { if (output is not null) DisposeQuietly(output); cipher.Dispose(); } throw; }
        }
        UpdateConnectionState();
        if (!_token.IsCancellationRequested) Changed?.Invoke(view);
    }

    public async Task ToggleMuteAsync()
    {
        await _muteCommand.WaitAsync(_token).ConfigureAwait(false);
        try
        {
            bool next;
            CancellationTokenSource? pendingVoice = null;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                next = !_muted;
                if (next)
                {
                    _muted = true; _captureEpoch++;
                    pendingVoice = _voiceRequest;
                    while (_outgoing.Reader.TryRead(out _)) { }
                }
            }
            if (pendingVoice is not null)
                try { pendingVoice.Cancel(); } catch (ObjectDisposedException) { /* The sender already completed this batch. */ }
            // Mute closes local capture immediately; failed unmute never opens
            // it. Only server acknowledgement permits future capture frames.
            await RequestAsync(ct => _api.SetCallMuteAsync(_id, next, ct)).ConfigureAwait(false);
            lock (_gate) { if (!_disposed) _muted = next; }
            // A confirmed write repairs transmission health even when muting
            // intentionally prevents any more voice batches from being created.
            MarkHealthy(sending: true);
        }
        finally { _muteCommand.Release(); }
    }

    public void SetSpeakerMuted(bool muted)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _speakerMuted = muted;
            foreach (var peer in _peers.Values) { peer.Output.Muted = muted; if (muted) peer.Output.Clear(); }
        }
    }

    private async Task<T> RequestAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token);
        deadline.CancelAfter(_policy.RequestTimeout);
        return await action(deadline.Token).ConfigureAwait(false);
    }
    private async Task RequestAsync(Func<CancellationToken, Task> action, CancellationToken operationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_token, operationToken);
        deadline.CancelAfter(_policy.RequestTimeout);
        await action(deadline.Token).ConfigureAwait(false);
    }
    private static bool IsTransient(Exception ex) => ex is HttpRequestException or OperationCanceledException ||
        ex is ChatApiException api && api.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;
    private void MarkFailure(bool sending)
    {
        lock (_gate)
        {
            if (_disposed) return;
            ref var since = ref (sending ? ref _sendFailure : ref _receiveFailure);
            since ??= _clock.GetUtcNow();
            if (_clock.GetUtcNow() - since >= _policy.RecoveryWindow)
                throw new InvalidOperationException("Arama bağlantısı geri kurulamadı.");
        }
        SetState(CallConnectionState.Reconnecting);
    }
    private void MarkHealthy(bool sending)
    {
        lock (_gate) { if (sending) _sendFailure = null; else _receiveFailure = null; }
        UpdateConnectionState();
    }
    private void CheckRecoveryDeadline()
    {
        lock (_gate)
        {
            if (_sendFailure is { } since && _clock.GetUtcNow() - since >= _policy.RecoveryWindow)
                throw new InvalidOperationException("Arama bağlantısı geri kurulamadı.");
        }
    }
    private bool ObsoleteVoiceBatch(long epoch)
    { lock (_gate) return _disposed || epoch != _captureEpoch; }
    private void UpdateConnectionState()
    {
        CallConnectionState next;
        lock (_gate) next = _disposed ? CallConnectionState.Ended : _sendFailure is not null || _receiveFailure is not null
            ? CallConnectionState.Reconnecting : _peers.Count > 0 ? CallConnectionState.Connected : CallConnectionState.Connecting;
        SetState(next);
    }
    private void SetState(CallConnectionState next)
    {
        lock (_gate) { if (_state == next || _disposed && next != CallConnectionState.Ended) return; _state = next; }
        ConnectionStateChanged?.Invoke(next);
    }
    private static string FriendlyFailure(Exception ex) => ex is ChatApiException api && api.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
        ? "Arama sona erdi." : ex is ChatApiException denied && denied.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? "Aramaya erişiminiz sona erdi." : ex is CryptographicException ? "Arama güvenliği doğrulanamadı." : ex.Message;
    private void End(string? failure)
    {
        lock (_gate)
        {
            if (_disposed || _ending) return;
            _ending = true;
        }
        // Publish the specific reason before Dispose's generic Ended event. UI
        // dispatchers otherwise close the session first and lose its real error.
        try { if (failure is not null) Failed?.Invoke(failure); }
        finally { Dispose(); }
    }
    public void Dispose()
    {
        ICallCapture? input;
        PeerAudio[] peers;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; input = _input; _input = null;
            peers = _peers.Values.ToArray(); _peers.Clear();
            _identity.Dispose(); _outgoing.Writer.TryComplete();
            while (_outgoing.Reader.TryRead(out _)) { }
        }
        _stop.Cancel();
        if (input is not null)
        {
            lock (_captureGate)
            {
                try { input.Stop(); } catch { /* Device may already be gone. */ }
                DisposeQuietly(input);
            }
        }
        foreach (var peer in peers) DisposeQuietly(peer);
        SetState(CallConnectionState.Ended);
        _ = Task.Run(DisposeCancellationAfterWorkersAsync);
    }
    private async Task DisposeCancellationAfterWorkersAsync()
    {
        // Run on the pool, not a form dispatcher that may already be closing.
        // StartAsync publishes its task under _gate before this snapshot wins it.
        Task? start;
        lock (_gate) start = _startTask;
        try { if (start is not null) await start.ConfigureAwait(false); } catch { }
        try { if (_workers is { } workers) await workers.ConfigureAwait(false); } catch { }
        _stop.Dispose();
        // Do not dispose the semaphore while a cancelled mute command releases it.
    }
    private static void DisposeQuietly(IDisposable resource)
    { try { resource.Dispose(); } catch { /* Still release other devices and cipher keys. */ } }
}
