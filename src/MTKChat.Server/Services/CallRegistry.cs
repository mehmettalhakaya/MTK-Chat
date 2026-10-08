using MTKChat.Contracts;
using MTKChat.Cryptography;

namespace MTKChat.Server.Services;

public sealed class CallFailure(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

// Short-lived ciphertext queues only. Calls never enter the message database or agent worker.
public sealed class CallRegistry(ChatState state, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Room> _rooms = new();
    private readonly Dictionary<Guid, DateTimeOffset> _lastStarted = new();
    private sealed class Member(CallPeer peer, DateTimeOffset now)
    {
        public CallPeer Peer = peer;
        public readonly DateTimeOffset JoinedAt = now;
        public DateTimeOffset Seen = now;
        public Queue<CallFrame> Frames { get; } = new();
        public DateTimeOffset Window = now;
        public int Sent;
    }
    private sealed class Room(Guid id, Guid conversation, string title, Guid caller, HashSet<Guid> invited, DateTimeOffset now)
    {
        public Guid Id = id, Conversation = conversation, Caller = caller;
        public string Title = title;
        public HashSet<Guid> Invited = invited;
        public HashSet<Guid> Declined = new();
        public Dictionary<Guid, DateTimeOffset> DeclinedAt = new();
        public Dictionary<Guid, Member> Members = new();
        public DateTimeOffset Created = now;
        public bool WasJoined;
        public bool WasConnected;
    }
    private bool Allowed(Guid user, Room room) => room.Invited.Contains(user) && !room.Declined.Contains(user) &&
        state.GetUser(user) is { IsAgent: false } && state.IsMember(user, room.Conversation) &&
        !state.IsBanned(user) && !state.IsMuted(user, out _) && !state.IsChatWriteRestricted(room.Conversation, user, out _) &&
        !room.Invited.Any(other => other != user && (state.GetBlocked(user).Contains(other) || state.GetBlocked(other).Contains(user)));
    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        foreach (var room in _rooms.Values.ToArray())
        {
            foreach (var id in room.Members.Where(p => now - p.Value.Seen > TimeSpan.FromSeconds(20) || !Allowed(p.Key, room)).Select(p => p.Key).ToArray())
                RemoveMember(room, id, now);
            // A live group call must not keep an unanswered third person busy
            // forever merely because two other participants already connected.
            if (now - room.Created >= TimeSpan.FromSeconds(60))
                foreach (var id in room.Invited.Where(id => !room.Members.ContainsKey(id) && !room.Declined.Contains(id)).ToArray())
                    RemoveMember(room, id, now);
            if (Finished(room) || !room.WasConnected &&
                (room.Declined.Contains(room.Caller) || now - room.Created > TimeSpan.FromSeconds(60))) _rooms.Remove(room.Id);
        }
        foreach (var id in _lastStarted.Where(p => now - p.Value > TimeSpan.FromMinutes(2)).Select(p => p.Key).ToArray()) _lastStarted.Remove(id);
    }
    private Room Require(Guid id, Guid user)
    {
        Sweep();
        if (!_rooms.TryGetValue(id, out var room)) throw new CallFailure(404, "Arama sona erdi.");
        if (!Allowed(user, room)) throw new CallFailure(403, "Bu aramaya erişim izniniz yok.");
        return room;
    }
    private CallView View(Room r) => new(r.Id, r.Conversation, r.Title, r.Caller, r.Created,
        r.Invited.Where(id => !r.Declined.Contains(id)).Select(state.GetUser).OfType<ChatUser>().ToArray(),
        r.Members.Values.Select(m => m.Peer).ToArray(),
        r.Invited.Select(id => new CallInvitation(id,
            r.Members.ContainsKey(id) ? "joined" : r.Declined.Contains(id) ? "declined" : "ringing",
            r.Members.TryGetValue(id, out var member) ? member.JoinedAt : r.DeclinedAt.GetValueOrDefault(id, r.Created))).ToArray());
    public CallView Start(Guid user, StartCallRequest request)
    {
        lock (_gate)
        {
            Sweep();
            if (request.Invitees is null || request.Invitees.Count is < 1 or > 5) throw new CallFailure(400, "Aramaya 1–5 kişi seçin.");
            var group = state.GetConversations(user).FirstOrDefault(c => c.Id == request.ConversationId)
                ?? throw new CallFailure(403, "Sohbet üyesi değilsiniz.");
            var invited = request.Invitees.Append(user).ToHashSet();
            if (invited.Count < 2) throw new CallFailure(400, "Aramak için başka bir kullanıcı seçin.");
            var room = new Room(Guid.NewGuid(), group.Id, group.Title, user, invited, _clock.GetUtcNow());
            if (invited.Any(id => !Allowed(id, room) || state.GetDevice(id) is null))
                throw new CallFailure(403, "Seçilen kişiler aramaya uygun değil; engel, susturma veya cihaz kaydı durumunu kontrol edin.");
            // Ringing invitees are busy too: never let two rooms compete for the
            // same microphone/accept screen before either user has joined.
            if (_rooms.Count >= 50 || _rooms.Values.Any(r => r.Conversation == group.Id ||
                    r.Invited.Any(id => !r.Declined.Contains(id) && invited.Contains(id))) ||
                _lastStarted.TryGetValue(user, out var last) && _clock.GetUtcNow() - last < TimeSpan.FromSeconds(10))
                throw new CallFailure(409, "Arama zaten var veya çok sık arama başlatıyorsunuz.");
            _rooms.Add(room.Id, room);
            _lastStarted[user] = _clock.GetUtcNow();
            return View(room);
        }
    }
    public IReadOnlyList<CallView> List(Guid user)
    {
        lock (_gate) { Sweep(); return _rooms.Values.Where(r => Allowed(user, r)).Select(View).ToArray(); }
    }
    public CallView Get(Guid id, Guid user) { lock (_gate) return View(Require(id, user)); }
    public CallView Join(Guid id, Guid user, CallJoin identity)
    {
        lock (_gate)
        {
            var r = Require(id, user);
            if (identity is null) throw new CallFailure(400, "Arama kimliği gerekli.");
            // A Join response can be lost after registration. Repeating the exact
            // signed ephemeral identity is idempotent; a different key is not.
            if (r.Members.TryGetValue(user, out var joined))
            {
                if (joined.Peer.Identity != identity) throw new CallFailure(409, "Zaten bir aramadasınız.");
                joined.Seen = _clock.GetUtcNow();
                return View(r);
            }
            if (_rooms.Values.Any(room => room.Id != id && room.Members.ContainsKey(user))) throw new CallFailure(409, "Zaten bir aramadasınız.");
            var device = state.GetDevice(user) ?? throw new CallFailure(403, "Cihaz kaydı gerekli.");
            try { CallIdentity.Verify(id, user, identity, device.SigningPublicKey); }
            catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException)
            { throw new CallFailure(400, "Arama anahtarının imzası geçersiz."); }
            r.Members.Add(user, new Member(new CallPeer(state.GetUser(user)!, identity, false), _clock.GetUtcNow()));
            r.WasJoined = true;
            r.WasConnected |= r.Members.Count > 1;
            return View(r);
        }
    }
    private Member Joined(Room room, Guid user)
    {
        if (!room.Members.TryGetValue(user, out var member)) throw new CallFailure(403, "Önce aramayı kabul edin.");
        member.Seen = _clock.GetUtcNow();
        return member;
    }
    public void Send(Guid id, Guid user, IReadOnlyList<CallFrame> frames)
    {
        lock (_gate)
        {
            var room = Require(id, user);
            var sender = Joined(room, user);
            if (frames is null || frames.Count is < 1 or > 20) throw new CallFailure(400, "Ses paketi sayısı geçersiz.");
            if (sender.Peer.Muted) return;
            var now = _clock.GetUtcNow();
            if (now - sender.Window >= TimeSpan.FromSeconds(1)) { sender.Window = now; sender.Sent = 0; }
            if ((sender.Sent += frames.Count) > 100) throw new CallFailure(429, "Ses aktarım sınırı aşıldı.");
            foreach (var frame in frames)
            {
                if (frame is null || frame.SenderId != user || frame.SenderSession != sender.Peer.Identity.SessionId ||
                    frame.Sequence < 0 || frame.Ciphertext is null || frame.Ciphertext.Length is < 4 or > 8536 ||
                    frame.Tag is null || frame.Tag.Length != 24 || frame.RecipientId == user || !room.Invited.Contains(frame.RecipientId))
                    throw new CallFailure(400, "Ses zarfı geçersiz.");
            }
            foreach (var frame in frames)
            {
                if (!room.Members.TryGetValue(frame.RecipientId, out var recipient) || recipient.Peer.Identity.SessionId != frame.RecipientSession) continue;
                // Bound latency and RAM for a slow recipient; old audio is less useful than current audio.
                while (recipient.Frames.Count >= 30) recipient.Frames.Dequeue();
                recipient.Frames.Enqueue(frame);
            }
        }
    }
    public IReadOnlyList<CallFrame> Receive(Guid id, Guid user)
    {
        lock (_gate)
        {
            var member = Joined(Require(id, user), user);
            var frames = member.Frames.ToArray();
            member.Frames.Clear();
            return frames;
        }
    }
    public void Mute(Guid id, Guid user, bool muted)
    {
        lock (_gate)
        {
            var room = Require(id, user);
            var member = Joined(room, user);
            member.Peer = member.Peer with { Muted = muted };
            if (muted) PurgeSenderFrames(room, user);
        }
    }
    private static void RemoveMember(Room room, Guid user, DateTimeOffset now)
    {
        room.Members.Remove(user);
        room.Declined.Add(user);
        room.DeclinedAt[user] = now;
        PurgeSenderFrames(room, user);
    }
    private static void PurgeSenderFrames(Room room, Guid user)
    {
        foreach (var member in room.Members.Values)
        {
            var keep = member.Frames.Where(f => f.SenderId != user).ToArray();
            member.Frames.Clear();
            foreach (var f in keep) member.Frames.Enqueue(f);
        }
    }
    // Explicit hang-up and disconnected peers must end an otherwise abandoned call identically.
    private static bool Finished(Room room) => room.WasJoined && (room.Members.Count == 0 ||
        room.Members.Count == 1 && room.Invited.All(p => room.Declined.Contains(p) || room.Members.ContainsKey(p)));
    public void Leave(Guid id, Guid user)
    {
        lock (_gate)
        {
            if (!_rooms.TryGetValue(id, out var room) || !room.Invited.Contains(user)) return;
            RemoveMember(room, user, _clock.GetUtcNow());
            if (Finished(room) || !room.WasConnected && room.Caller == user) _rooms.Remove(id);
        }
    }

    public void RevokeConversationMember(Guid conversationId, Guid userId)
    {
        lock (_gate)
        {
            foreach (var room in _rooms.Values.Where(room => room.Conversation == conversationId && room.Invited.Contains(userId)).ToArray())
            {
                // Decline as well as removing queues: an immediate group rejoin must
                // not revive the old call identity or previously queued voice data.
                RemoveMember(room, userId, _clock.GetUtcNow());
                if (Finished(room) || !room.WasConnected && room.Caller == userId) _rooms.Remove(room.Id);
            }
        }
    }
}
