using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Desktop;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class CallReliabilityTests
{
    [Fact]
    public void ExactSignedJoinIsIdempotentButDifferentSessionIsRejected()
    {
        using var fixture = new RegistryFixture();
        var call = fixture.Calls.Start(fixture.A.Id, new(fixture.Room.Id, [fixture.B.Id]));
        using var identity = new CallIdentity(call.Id, fixture.A.Id, fixture.Da.SigningKey);
        var first = fixture.Calls.Join(call.Id, fixture.A.Id, identity.Join);
        Assert.Single(first.Peers);
        Assert.Single(fixture.Calls.Join(call.Id, fixture.A.Id, identity.Join).Peers);
        using var other = new CallIdentity(call.Id, fixture.A.Id, fixture.Da.SigningKey);
        Assert.Equal(409, Assert.Throws<CallFailure>(() => fixture.Calls.Join(call.Id, fixture.A.Id, other.Join)).Status);
    }

    [Fact]
    public void PendingInviteeCannotBeCalledInAnotherRoomAndCallerCanCancelBeforeJoin()
    {
        using var f = new RegistryFixture();
        var otherRoom = f.State.CreateConversation(f.C.Id, new("Other", [f.B.Id]))!;
        var call = f.Calls.Start(f.A.Id, new(f.Room.Id, [f.B.Id]));
        Assert.Equal(409, Assert.Throws<CallFailure>(() => f.Calls.Start(f.C.Id, new(otherRoom.Id, [f.B.Id]))).Status);
        f.Calls.Leave(call.Id, f.A.Id);
        Assert.Empty(f.Calls.List(f.B.Id));
        Assert.NotNull(f.Calls.Start(f.C.Id, new(otherRoom.Id, [f.B.Id])));
    }

    [Fact]
    public void GroupReportsDeclinedAndJoinedStatesWithoutReinvitingDeclinedMember()
    {
        using var f = new RegistryFixture();
        var call = f.Calls.Start(f.A.Id, new(f.Room.Id, [f.B.Id, f.C.Id]));
        Assert.All(call.Invitations!, invitation => Assert.Equal("ringing", invitation.State));
        using var identity = new CallIdentity(call.Id, f.A.Id, f.Da.SigningKey);
        f.Calls.Join(call.Id, f.A.Id, identity.Join);
        f.Calls.Leave(call.Id, f.B.Id);
        var view = f.Calls.Get(call.Id, f.A.Id);
        Assert.Equal("declined", view.Invitations!.Single(i => i.UserId == f.B.Id).State);
        Assert.Equal("joined", view.Invitations!.Single(i => i.UserId == f.A.Id).State);
        Assert.DoesNotContain(view.Invitees, user => user.Id == f.B.Id);
        Assert.Equal(403, Assert.Throws<CallFailure>(() => f.Calls.Get(call.Id, f.B.Id)).Status);
        f.Calls.Leave(call.Id, f.A.Id);
        Assert.Empty(f.Calls.List(f.C.Id));
    }

    [Fact]
    public void MutingPurgesQueuedOldAudioWithoutErasingAnotherSendersAudio()
    {
        using var f = new RegistryFixture();
        var call = f.Calls.Start(f.A.Id, new(f.Room.Id, [f.B.Id, f.C.Id]));
        using var a = new CallIdentity(call.Id, f.A.Id, f.Da.SigningKey);
        using var b = new CallIdentity(call.Id, f.B.Id, f.Db.SigningKey);
        using var c = new CallIdentity(call.Id, f.C.Id, f.Dc.SigningKey);
        f.Calls.Join(call.Id, f.A.Id, a.Join); f.Calls.Join(call.Id, f.B.Id, b.Join); f.Calls.Join(call.Id, f.C.Id, c.Join);
        using var ab = a.Connect(f.B.Id, b.Join, f.Db.ExportSigningPublicKey());
        using var cb = c.Connect(f.B.Id, b.Join, f.Db.ExportSigningPublicKey());
        f.Calls.Send(call.Id, f.A.Id, [ab.Encrypt(new byte[3200])]);
        f.Calls.Send(call.Id, f.C.Id, [cb.Encrypt(new byte[3200])]);
        f.Calls.Mute(call.Id, f.A.Id, true);
        Assert.Equal(f.C.Id, Assert.Single(f.Calls.Receive(call.Id, f.B.Id)).SenderId);
    }

    [Fact]
    public void UnansweredGroupInviteExpiresWithoutEndingOtherConnectedParticipants()
    {
        var clock = new MutableClock();
        using var f = new RegistryFixture(clock);
        var call = f.Calls.Start(f.A.Id, new(f.Room.Id, [f.B.Id, f.C.Id]));
        using var a = new CallIdentity(call.Id, f.A.Id, f.Da.SigningKey);
        using var c = new CallIdentity(call.Id, f.C.Id, f.Dc.SigningKey);
        f.Calls.Join(call.Id, f.A.Id, a.Join); f.Calls.Join(call.Id, f.C.Id, c.Join);
        for (var i = 0; i < 6; i++)
        { clock.Now += TimeSpan.FromSeconds(10); f.Calls.Receive(call.Id, f.A.Id); f.Calls.Receive(call.Id, f.C.Id); }
        var stillActive = f.Calls.Get(call.Id, f.A.Id);
        Assert.Equal(2, stillActive.Peers.Count);
        Assert.Equal("declined", stillActive.Invitations!.Single(i => i.UserId == f.B.Id).State);
        Assert.Empty(f.Calls.List(f.B.Id));
        var another = f.State.UpsertSiteUser(Guid.NewGuid(), "Other", "other@example.test", "user");
        using var device = DeviceIdentity.Create();
        f.State.RegisterDevice(another.Id, new("qa", device.ExportEncryptionPublicKey(), device.ExportSigningPublicKey()));
        var room = f.State.CreateConversation(another.Id, new("Other room", [f.B.Id]))!;
        Assert.NotNull(f.Calls.Start(another.Id, new(room.Id, [f.B.Id])));
    }

    [Fact]
    public async Task CaptureRequiresExplicitStartAndConnectionRequiresVerifiedRemotePeer()
    {
        using var f = new SessionFixture();
        f.Handler.WithPeer = false;
        Assert.Null(f.Audio.Capture);
        Assert.Equal(CallConnectionState.Connecting, f.Session.State);
        await f.Session.StartAsync();
        Assert.Equal(1, f.Audio.Capture!.Starts);
        Assert.Empty(f.Session.Codes);
        Assert.Equal(CallConnectionState.Connecting, f.Session.State);
        f.Handler.WithPeer = true;
        await Until(() => f.Session.State == CallConnectionState.Connected);
        Assert.Single(f.Session.Codes);
        Assert.Equal(1, f.Audio.Capture.Starts);
    }

    [Fact]
    public async Task ConcurrentStartUsesOneJoinAndOneCapture()
    {
        using var f = new SessionFixture();
        var first = f.Session.StartAsync(); var second = f.Session.StartAsync();
        await Task.WhenAll(first, second);
        Assert.Same(first, second);
        Assert.Equal(1, f.Handler.Joins);
        Assert.Equal(1, f.Audio.Capture!.Starts);
    }

    [Fact]
    public async Task TemporaryKeyLookupFailureRecoversBeforeOpeningCapture()
    {
        using var f = new SessionFixture();
        f.Handler.FailDevice = true;
        var pending = f.Session.StartAsync();
        await Until(() => f.Session.State == CallConnectionState.Reconnecting);
        Assert.Null(f.Audio.Capture);
        f.Handler.FailDevice = false;
        await pending;
        Assert.Equal(1, f.Handler.Joins);
        Assert.Equal(CallConnectionState.Connected, f.Session.State);
    }

    [Fact]
    public async Task ForgedPeerIdentityNeverOpensCaptureOrPlayback()
    {
        using var f = new SessionFixture();
        f.Handler.ForgeIdentity = true;
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => f.Session.StartAsync());
        Assert.Null(f.Audio.Capture);
        Assert.Empty(f.Audio.Playbacks);
        Assert.Equal(CallConnectionState.Ended, f.Session.State);
    }

    [Fact]
    public async Task AudioCallbackFailureEndsOnceAndCleansResourcesOffCallback()
    {
        using var f = new SessionFixture();
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Audio.Capture!.Fail(); f.Audio.Capture.Fail();
        await Until(() => !errors.IsEmpty && f.Session.State == CallConnectionState.Ended);
        Assert.Equal(CallConnectionState.Ended, f.Session.State);
        Assert.Single(errors);
        Assert.True(f.Audio.Capture.Disposed);
    }

    [Fact]
    public async Task ChangingSignedIdentityWithinSameSessionTriggersReverification()
    {
        using var f = new SessionFixture();
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Handler.ForgeIdentity = true;
        await Until(() => f.Session.State == CallConnectionState.Ended && !errors.IsEmpty);
        Assert.Contains("güvenliği", errors.Single());
        Assert.All(f.Audio.Playbacks, playback => Assert.True(playback.Disposed));
    }

    [Fact]
    public async Task OmittedPeerCannotReuseRetiredSessionAndResetGcmNonceSequence()
    {
        using var f = new SessionFixture();
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Audio.Capture!.Emit(); await Until(() => f.Handler.Sent.Count > 0);
        f.Handler.WithPeer = false;
        await Until(() => f.Session.Codes.Count == 0);
        Assert.True(f.Audio.Playbacks.Single().Disposed);
        f.Handler.WithPeer = true;
        await Until(() => f.Session.State == CallConnectionState.Ended && !errors.IsEmpty);
        Assert.Single(f.Audio.Playbacks); // No fresh cipher/output for the old key.
        Assert.Contains("güvenliği", errors.Single());
        var sent = f.Handler.Sent.Count; f.Audio.Capture.Emit();
        await Task.Delay(25); Assert.Equal(sent, f.Handler.Sent.Count);
    }

    [Fact]
    public async Task LostJoinResponseIsConfirmedWithoutReplayingItsPost()
    {
        using var f = new SessionFixture();
        f.Handler.LoseFirstJoinResponse = true;
        await f.Session.StartAsync();
        Assert.Equal(1, f.Handler.Joins);
        Assert.Equal(CallConnectionState.Connected, f.Session.State);
        Assert.Equal(1, f.Audio.Capture!.Starts);
    }

    [Fact]
    public async Task LostJoinResponseAndFailedRosterProbeNeverCauseBlindPostReplay()
    {
        using var f = new SessionFixture();
        f.Handler.LoseFirstJoinResponse = true; f.Handler.FailReads = true;
        var pending = f.Session.StartAsync();
        await Until(() => f.Handler.Joins == 1 && f.Session.State == CallConnectionState.Reconnecting);
        await Task.Delay(30);
        Assert.Equal(1, f.Handler.Joins);
        Assert.Null(f.Audio.Capture);
        f.Handler.FailReads = false; await pending;
        Assert.Equal(1, f.Handler.Joins);
        Assert.Equal(CallConnectionState.Connected, f.Session.State);
    }

    [Fact]
    public async Task StartingInvitationHonorsCallerCancellationWithoutOpeningAudio()
    {
        using var f = new SessionFixture();
        f.Handler.HoldStart = true;
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Api.StartCallAsync(new(Guid.NewGuid(), [f.Handler.Peer.Id]), cancelled.Token));
        Assert.Null(f.Audio.Capture);
        Assert.Equal(0, f.Handler.Joins);
    }

    [Fact]
    public async Task DisposalCancelsPendingJoinAndNeverCreatesCapture()
    {
        using var f = new SessionFixture();
        f.Handler.HoldJoin = true;
        var pending = f.Session.StartAsync();
        await Until(() => f.Handler.Joins == 1);
        f.Session.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(f.Audio.Capture);
        Assert.Equal(CallConnectionState.Ended, f.Session.State);
    }

    [Fact]
    public async Task NativeStartFailureRacingDisposalHasExactlyOneCaptureOwner()
    {
        using var f = new SessionFixture();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        f.Audio.CaptureStart = () =>
        {
            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(3)));
            throw new InvalidOperationException("Scripted native start failure.");
        };
        var starting = Task.Run(() => f.Session.StartAsync());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        var disposing = Task.Run(f.Session.Dispose);
        await Until(() => f.Session.Codes.Count == 0); // Dispose detached/owns capture, then waits for native start.
        release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => starting);
        await disposing;
        Assert.Equal(1, f.Audio.Capture!.Disposals);
        Assert.Equal(1, f.Audio.Capture.Stops);
    }

    [Fact]
    public async Task SynchronousHttpCannotHoldCipherLockAcrossNativeStartCallback()
    {
        using var f = new SessionFixture();
        using var callbackFinished = new ManualResetEventSlim();
        f.Audio.CaptureStart = () =>
        {
            ThreadPool.QueueUserWorkItem(_ => { f.Audio.Capture!.Emit(); callbackFinished.Set(); });
            Assert.True(callbackFinished.Wait(TimeSpan.FromSeconds(3)));
        };
        await f.Session.StartAsync();
        await Until(() => !f.Handler.Sent.IsEmpty);
        Assert.Equal(CallConnectionState.Connected, f.Session.State);
    }

    [Fact]
    public async Task TemporaryReadFailureReconnectsWithoutOpeningAnotherMicrophone()
    {
        using var f = new SessionFixture();
        var states = new ConcurrentQueue<CallConnectionState>();
        var errors = new ConcurrentQueue<string>();
        f.Session.ConnectionStateChanged += states.Enqueue;
        f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Handler.FailReads = true;
        await Until(() => states.Contains(CallConnectionState.Reconnecting));
        Assert.Empty(errors);
        f.Handler.FailReads = false;
        await Until(() => f.Session.State == CallConnectionState.Connected);
        Assert.Equal(1, f.Audio.Capture!.Starts);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task AuthFailureEndsImmediatelyRatherThanRetryingForRecoveryWindow()
    {
        using var f = new SessionFixture();
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Handler.ReadStatus = HttpStatusCode.Forbidden;
        await Until(() => f.Session.State == CallConnectionState.Ended && !errors.IsEmpty);
        Assert.Single(errors);
        Assert.Contains("erişiminiz", errors.Single());
        Assert.True(f.Audio.Capture!.Disposed);
        Assert.All(f.Audio.Playbacks, p => Assert.True(p.Disposed));
    }

    [Fact]
    public async Task TerminalReasonPrecedesGenericEndedNotification()
    {
        using var f = new SessionFixture();
        var notifications = new ConcurrentQueue<string>();
        f.Session.Failed += _ => notifications.Enqueue("reason");
        f.Session.ConnectionStateChanged += state => { if (state == CallConnectionState.Ended) notifications.Enqueue("ended"); };
        await f.Session.StartAsync();
        f.Handler.ReadStatus = HttpStatusCode.NotFound;
        await Until(() => notifications.Count == 2);
        Assert.Equal(new[] { "reason", "ended" }, notifications.ToArray());
    }

    [Fact]
    public async Task NativeStartupFailurePublishesSpecificReasonBeforeEnded()
    {
        using var f = new SessionFixture();
        var notifications = new ConcurrentQueue<string>();
        const string reason = "Scripted microphone startup failure.";
        f.Audio.CaptureStart = () => throw new InvalidOperationException(reason);
        f.Session.Failed += message => notifications.Enqueue(message);
        f.Session.ConnectionStateChanged += state => { if (state == CallConnectionState.Ended) notifications.Enqueue("ended"); };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Session.StartAsync());
        Assert.Equal(reason, error.Message);
        Assert.Equal(new[] { reason, "ended" }, notifications.ToArray());
        Assert.Equal(1, f.Audio.Capture!.Disposals);
        Assert.All(f.Audio.Playbacks, playback => Assert.True(playback.Disposed));
    }

    [Fact]
    public async Task PersistentTransientFailureHasBoundedRecoveryAndCleanup()
    {
        using var f = new SessionFixture(new CallSessionPolicy
        { RequestTimeout = TimeSpan.FromMilliseconds(100), RecoveryWindow = TimeSpan.FromMilliseconds(80), PollInterval = TimeSpan.FromMilliseconds(5), RetryDelay = TimeSpan.FromMilliseconds(10) });
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Handler.FailReads = true;
        await Until(() => f.Session.State == CallConnectionState.Ended && !errors.IsEmpty);
        Assert.Single(errors);
        Assert.True(f.Audio.Capture!.Disposed);
    }

    [Fact]
    public async Task MuteIsImmediateAndFailedUnmuteCannotLeakCaptureFrames()
    {
        using var f = new SessionFixture();
        await f.Session.StartAsync();
        f.Audio.Capture!.Emit(); await Until(() => f.Handler.Sent.Count > 0);
        f.Handler.HoldMute = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var mute = f.Session.ToggleMuteAsync();
        await Until(() => f.Handler.MuteRequests > 0);
        Assert.True(f.Session.Muted);
        var count = f.Handler.Sent.Count;
        f.Audio.Capture.Emit(); await Task.Delay(25);
        Assert.Equal(count, f.Handler.Sent.Count);
        f.Handler.HoldMute.SetResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        await mute;
        f.Handler.HoldMute = null; f.Handler.FailMute = true;
        await Assert.ThrowsAsync<ChatApiException>(() => f.Session.ToggleMuteAsync());
        Assert.True(f.Session.Muted);
        f.Audio.Capture.Emit(); await Task.Delay(25);
        Assert.Equal(count, f.Handler.Sent.Count);
    }

    [Fact]
    public async Task UncertainVoicePostDropsOldBatchInsteadOfReplayingIt()
    {
        using var f = new SessionFixture();
        await f.Session.StartAsync();
        f.Handler.FailNextSend = true;
        f.Audio.Capture!.Emit(); await Until(() => f.Handler.Sent.Count == 1);
        await Task.Delay(30);
        Assert.Single(f.Handler.Sent);
        f.Audio.Capture.Emit(); await Until(() => f.Handler.Sent.Count == 2);
        var sent = f.Handler.Sent.ToArray();
        Assert.NotEqual(sent[0].Sequence, sent[1].Sequence);
        Assert.NotEqual(sent[0].Ciphertext, sent[1].Ciphertext);
    }

    [Fact]
    public async Task ConfirmedMuteRepairsFailedSendWhileNoNewVoiceFramesAreProduced()
    {
        using var f = new SessionFixture();
        await f.Session.StartAsync();
        f.Handler.FailNextSend = true;
        f.Audio.Capture!.Emit();
        await Until(() => f.Session.State == CallConnectionState.Reconnecting);
        await f.Session.ToggleMuteAsync();
        await Until(() => f.Session.State == CallConnectionState.Connected);
        Assert.True(f.Session.Muted);
        Assert.Single(f.Handler.Sent);
    }

    [Fact]
    public async Task LateOldVoiceFailureCannotUndoSuccessfulMuteAcknowledgement()
    {
        using var f = new SessionFixture(new CallSessionPolicy
        { RecoveryWindow = TimeSpan.FromMilliseconds(80), PollInterval = TimeSpan.FromMilliseconds(5), RetryDelay = TimeSpan.FromMilliseconds(5) });
        await f.Session.StartAsync();
        f.Handler.HoldSend = new(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Audio.Capture!.Emit(); await Until(() => f.Handler.Sent.Count == 1);
        await f.Session.ToggleMuteAsync();
        Assert.Equal(CallConnectionState.Connected, f.Session.State);
        f.Handler.HoldSend.SetException(new HttpRequestException("Scripted late failure from old capture epoch."));
        await Task.Delay(150); // Longer than recovery: a stale failure must not arm it.
        Assert.True(f.Session.Muted);
        Assert.Equal(CallConnectionState.Connected, f.Session.State);
    }

    [Fact]
    public async Task SendFailureDeadlineIsEnforcedEvenWithoutAnotherCapturedBatch()
    {
        using var f = new SessionFixture(new CallSessionPolicy
        { RecoveryWindow = TimeSpan.FromMilliseconds(80), PollInterval = TimeSpan.FromMilliseconds(5), RetryDelay = TimeSpan.FromMilliseconds(5) });
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        f.Handler.FailNextSend = true; f.Audio.Capture!.Emit();
        await Until(() => f.Session.State == CallConnectionState.Ended && !errors.IsEmpty);
        Assert.Single(f.Handler.Sent);
        Assert.Single(errors);
    }

    [Fact]
    public async Task ForgedAndReplayPacketsAreNotPlayedAndSpeakerMutePurgesBuffer()
    {
        using var f = new SessionFixture();
        await f.Session.StartAsync();
        using var remote = f.Handler.PeerIdentity.Connect(f.Handler.Self.Id, f.Handler.Join!, f.SelfDevice.ExportSigningPublicKey());
        var frame = remote.Encrypt(new byte[3200]);
        f.Handler.Incoming.Enqueue(frame with { Tag = Convert.ToBase64String(new byte[16]) });
        f.Handler.Incoming.Enqueue(frame); f.Handler.Incoming.Enqueue(frame);
        await Until(() => f.Audio.Playbacks.Single().Packets == 1);
        f.Session.SetSpeakerMuted(true);
        Assert.True(f.Session.SpeakerMuted);
        Assert.True(f.Audio.Playbacks.Single().Clears > 0);
        f.Handler.Incoming.Enqueue(remote.Encrypt(new byte[3200]));
        await Task.Delay(30);
        Assert.Equal(1, f.Audio.Playbacks.Single().Packets);
        f.Session.SetSpeakerMuted(false);
        f.Handler.Incoming.Enqueue(remote.Encrypt(new byte[3200]));
        await Until(() => f.Audio.Playbacks.Single().Packets == 2);
    }

    [Fact]
    public async Task LegacyRelayWithoutInvitationMetadataStillExpiresUnansweredCall()
    {
        using var f = new SessionFixture();
        f.Handler.WithPeer = false;
        f.Handler.CreatedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(61);
        var errors = new ConcurrentQueue<string>(); f.Session.Failed += errors.Enqueue;
        await f.Session.StartAsync();
        await Until(() => f.Session.State == CallConnectionState.Ended && !errors.IsEmpty);
        Assert.Contains("yanıtlanmadı", errors.Single());
    }

    private static async Task Until(Func<bool> predicate)
    {
        var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(4);
        while (!predicate()) { if (DateTimeOffset.UtcNow > until) throw new TimeoutException("Scripted call condition timed out."); await Task.Delay(5); }
    }

    private sealed class RegistryFixture : IDisposable
    {
        internal readonly ChatState State = new();
        internal readonly DeviceIdentity Da = DeviceIdentity.Create(), Db = DeviceIdentity.Create(), Dc = DeviceIdentity.Create();
        internal readonly ChatUser A, B, C;
        internal readonly ConversationSummary Room;
        internal readonly CallRegistry Calls;
        internal RegistryFixture(TimeProvider? clock = null)
        {
            A = Add(Da, "Alice"); B = Add(Db, "Bob"); C = Add(Dc, "Cem");
            Room = State.CreateConversation(A.Id, new("Group", [B.Id, C.Id]))!;
            Calls = new(State, clock);
        }
        private ChatUser Add(DeviceIdentity device, string name)
        {
            var user = State.UpsertSiteUser(Guid.NewGuid(), name, name + "@example.test", "user");
            State.RegisterDevice(user.Id, new("qa", device.ExportEncryptionPublicKey(), device.ExportSigningPublicKey()));
            return user;
        }
        public void Dispose() { Da.Dispose(); Db.Dispose(); Dc.Dispose(); }
    }
    private sealed class MutableClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SessionFixture : IDisposable
    {
        internal readonly DeviceIdentity SelfDevice = DeviceIdentity.Create(), PeerDevice = DeviceIdentity.Create();
        internal readonly ScriptedRelay Handler;
        internal readonly ChatApiClient Api;
        internal readonly FakeAudio Audio = new();
        internal readonly CallSession Session;
        internal SessionFixture(CallSessionPolicy? policy = null)
        {
            Handler = new(PeerDevice);
            Api = new("https://call-qa.invalid/", Handler, new ChatRequestPolicy { RetryDelay = TimeSpan.Zero });
            Session = new(Api, Handler.Id, Handler.Self.Id, SelfDevice, Audio, policy ?? new CallSessionPolicy
            { PollInterval = TimeSpan.FromMilliseconds(5), RosterInterval = TimeSpan.FromMilliseconds(10), RetryDelay = TimeSpan.FromMilliseconds(5) });
        }
        public void Dispose() { Session.Dispose(); Api.Dispose(); Handler.PeerIdentity.Dispose(); SelfDevice.Dispose(); PeerDevice.Dispose(); }
    }
    private sealed class ScriptedRelay : HttpMessageHandler
    {
        internal readonly Guid Id = Guid.NewGuid();
        internal readonly ChatUser Self = new(Guid.NewGuid(), "Alice", "alice@example.test", false, null);
        internal readonly ChatUser Peer = new(Guid.NewGuid(), "Bob", "bob@example.test", false, null);
        internal readonly CallIdentity PeerIdentity;
        private readonly DeviceIdentity _peerDevice;
        internal readonly ConcurrentQueue<CallFrame> Sent = new(), Incoming = new();
        internal CallJoin? Join;
        internal volatile bool WithPeer = true, LoseFirstJoinResponse, HoldJoin, HoldStart, FailReads, FailMute, FailNextSend, FailDevice, ForgeIdentity;
        internal HttpStatusCode ReadStatus = HttpStatusCode.OK;
        internal DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        internal int Joins, MuteRequests;
        internal TaskCompletionSource<HttpResponseMessage>? HoldMute;
        internal TaskCompletionSource<HttpResponseMessage>? HoldSend;
        internal ScriptedRelay(DeviceIdentity peerDevice) { _peerDevice = peerDevice; PeerIdentity = new(Id, Peer.Id, peerDevice.SigningKey); }
        private CallView View() => new(Id, Guid.NewGuid(), "Test call", Self.Id, CreatedAt, [Self, Peer],
            (Join is null ? Array.Empty<CallPeer>() : new[] { new CallPeer(Self, Join, false) }).Concat(
                WithPeer ? new[] { new CallPeer(Peer, ForgeIdentity ? PeerIdentity.Join with { Signature = Convert.ToBase64String(new byte[64]) } : PeerIdentity.Join, false) } : Array.Empty<CallPeer>()).ToArray());
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/api/calls") && request.Method == HttpMethod.Post && HoldStart)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (path.EndsWith("/join"))
            {
                Interlocked.Increment(ref Joins);
                if (HoldJoin) await Task.Delay(Timeout.Infinite, cancellationToken);
                Join = await request.Content!.ReadFromJsonAsync<CallJoin>(cancellationToken);
                if (LoseFirstJoinResponse) { LoseFirstJoinResponse = false; throw new HttpRequestException("Lost scripted join response."); }
                return Json(View());
            }
            if (path.EndsWith("/mute"))
            {
                Interlocked.Increment(ref MuteRequests);
                if (HoldMute is { } held) return await held.Task.WaitAsync(cancellationToken);
                return new(FailMute ? HttpStatusCode.Forbidden : HttpStatusCode.NoContent)
                { Content = JsonContent.Create(new ApiError("qa", "Scripted mute rejection")) };
            }
            if (path.EndsWith("/device")) return FailDevice
                ? new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new ApiError("qa", "Scripted key lookup failure")) }
                : Json(new DeviceKeyBundle(Peer.Id, Guid.NewGuid(), _peerDevice.ExportEncryptionPublicKey(), _peerDevice.ExportSigningPublicKey()));
            if (request.Method == HttpMethod.Get && (FailReads || ReadStatus != HttpStatusCode.OK))
                return new(FailReads ? HttpStatusCode.ServiceUnavailable : ReadStatus) { Content = JsonContent.Create(new ApiError("qa", "Scripted read failure")) };
            if (path.EndsWith("/frames") && request.Method == HttpMethod.Post)
            {
                var frames = await request.Content!.ReadFromJsonAsync<CallFrame[]>(cancellationToken);
                foreach (var frame in frames!) Sent.Enqueue(frame);
                // Deliberately ignore cancellation to emulate a completed server
                // write whose old response arrives after mute acknowledgement.
                if (HoldSend is { } pendingSend) return await pendingSend.Task;
                if (FailNextSend) { FailNextSend = false; throw new HttpRequestException("Lost scripted frame response."); }
                return new(HttpStatusCode.NoContent);
            }
            if (path.EndsWith("/frames"))
            {
                var list = new List<CallFrame>(); while (Incoming.TryDequeue(out var frame)) list.Add(frame);
                return Json(list);
            }
            return Json(View());
        }
        private static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
    private sealed class FakeAudio : ICallAudioFactory
    {
        internal FakeCapture? Capture;
        internal Action? CaptureStart;
        internal readonly ConcurrentBag<FakePlayback> Playbacks = new();
        public ICallCapture CreateCapture(Action<byte[], int> data, Action<Exception?> stopped) => Capture = new(data, stopped, CaptureStart);
        public ICallPlayback CreatePlayback() { var playback = new FakePlayback(); Playbacks.Add(playback); return playback; }
    }
    private sealed class FakeCapture(Action<byte[], int> data, Action<Exception?> stopped, Action? start = null) : ICallCapture
    {
        internal int Starts, Stops, Disposals;
        internal bool Disposed;
        public void Start() { ObjectDisposedException.ThrowIf(Disposed, this); Interlocked.Increment(ref Starts); start?.Invoke(); }
        public void Stop() => Interlocked.Increment(ref Stops);
        public void Dispose() { Disposed = true; Interlocked.Increment(ref Disposals); }
        internal void Emit() => data(new byte[3200], 3200);
        internal void Fail() => stopped(new InvalidOperationException("Scripted capture disconnected."));
    }
    private sealed class FakePlayback : ICallPlayback
    {
        internal int Packets, Clears;
        internal bool Disposed;
        public TimeSpan BufferedDuration => TimeSpan.Zero;
        public bool Muted { set { } }
        public void Start() { }
        public void Clear() => Interlocked.Increment(ref Clears);
        public void Add(byte[] pcm) { Assert.False(Disposed); Interlocked.Increment(ref Packets); }
        public void Dispose() => Disposed = true;
    }
}
