namespace MTKChat.Contracts;

// Pins contain only authenticated routing metadata. Message bodies and media
// still use the existing recipient-specific end-to-end encrypted envelopes.
public sealed record PinMessageRequest(Guid MessageId, int DurationHours = 168);
public sealed record PinnedMessageView(Guid MessageId, Guid ConversationId, Guid PinnedBy,
    DateTimeOffset PinnedAt, DateTimeOffset ExpiresAt);
