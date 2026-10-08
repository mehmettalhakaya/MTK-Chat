namespace MTKChat.Contracts;

public sealed record StartCallRequest(Guid ConversationId, IReadOnlyList<Guid> Invitees);
public sealed record CallJoin(Guid SessionId, string PublicKey, string Signature);
public sealed record CallPeer(ChatUser User, CallJoin Identity, bool Muted);
// Optional metadata preserves old client/relay compatibility; no voice plaintext.
public sealed record CallInvitation(Guid UserId, string State, DateTimeOffset UpdatedAt);
public sealed record CallView(Guid Id, Guid ConversationId, string Title, Guid CallerId,
    DateTimeOffset CreatedAt, IReadOnlyList<ChatUser> Invitees, IReadOnlyList<CallPeer> Peers,
    IReadOnlyList<CallInvitation>? Invitations = null);
public sealed record CallMute(bool Muted);
public sealed record CallFrame(Guid SenderId, Guid SenderSession, Guid RecipientId, Guid RecipientSession,
    long Sequence, string Ciphertext, string Tag);
