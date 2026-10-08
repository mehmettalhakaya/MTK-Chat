namespace MTKChat.Contracts;

// ExpiresAt stays non-null for older clients. Unlimited entries use MaxValue only as
// a compatibility date; NeverExpires is the authoritative, persisted expiry policy.
public sealed record GroupInviteResult(Guid ConversationId, string Title, string InviteUrl,
    DateTimeOffset ExpiresAt, bool NeverExpires = false);

// Deliberately omit member identities, photos, email addresses and encrypted history from
// previews: holding an invitation alone must not make someone a group member.
public sealed record GroupInvitePreview(Guid ConversationId, string Title, DateTimeOffset ExpiresAt,
    bool AlreadyMember, bool NeverExpires = false);

public sealed record GroupInviteTokenRequest(string Token);

// Null URL identifies a migrated hash-only link: its validity is retained without
// inventing a replacement token. A GET must never rotate an existing invitation.
public sealed record GroupInviteEntry(Guid Id, Guid ConversationId, string? InviteUrl,
    DateTimeOffset? CreatedAt, DateTimeOffset ExpiresAt, bool NeverExpires = false);

// 0 is a duration-less sentinel only with NeverExpires=true. Omitting the flag
// retains the existing finite duration validation and seven-day creation default.
public sealed record CreateGroupInviteRequest(int DurationMinutes = 10080, bool NeverExpires = false);
public sealed record ChangeGroupInviteDurationRequest(int DurationMinutes, bool NeverExpires = false);
