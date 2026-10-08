namespace MTKChat.Contracts;

public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string AccessToken, ChatUser User);
// Optional trailing fields keep old clients/snapshots compatible. DisplayName is
// still the site's username; editable real names must not rename the identity.
public sealed record ChatUser(Guid Id, string DisplayName, string Email, bool IsAgent, string? AvatarColor,
    string Role = "user", string? PhotoVersion = null, string? FirstName = null, string? LastName = null);
public sealed record ProfileNameChangeRequest(string FirstName, string LastName);
public sealed record AdminUserView(ChatUser User, bool IsBanned, DateTimeOffset? MutedUntil);
public sealed record RoleChangeRequest(string Role);
public sealed record ModerationRequest(string Action, int? DurationMinutes);
public sealed record ChatModerationRequest(string Action, int? DurationMinutes);
public sealed record ChatModerationView(Guid UserId, bool IsMuted, DateTimeOffset? MutedUntil, bool IsBanned);
public sealed record PresenceView(ChatUser User, bool IsOnline, bool IsInConversation, bool IsViewingConversation,
    DateTimeOffset? LastSeenAt, string GroupRole = "user");
public sealed record PrivacySettings(bool ShowLastSeen = true, bool SendReadReceipts = true);
public sealed record GroupRoleChangeRequest(string Role);
public sealed record FileUploadResult(string StorageToken);
public sealed record EncryptedFileDescriptor(string FileName, string ContentType, long Size, string StorageToken,
    string Key, string Nonce, string Tag);
public sealed record GroupMemberView(ChatUser User, string GroupRole, ChatModerationView Moderation);
public sealed record GroupManagementView(Guid Id, string Title, string MyRole, IReadOnlyList<GroupMemberView> Members,
    bool IsSiteAdmin = false);

public sealed record RegisterDeviceRequest(
    string DeviceName,
    string EncryptionPublicKey,
    string SigningPublicKey);

public sealed record DeviceKeyBundle(
    Guid UserId,
    Guid DeviceId,
    string EncryptionPublicKey,
    string SigningPublicKey);

public sealed record CreateConversationRequest(string Title, IReadOnlyList<Guid> ParticipantIds);
public sealed record RenameConversationRequest(string Title);
// A title-only response never grants a nonmember site admin access to messages or keys.
public sealed record GroupTitleResult(Guid ConversationId, string Title);
public sealed record CreateDirectConversationRequest(Guid UserId);
public sealed record ConversationSummary(
    Guid Id,
    string Title,
    IReadOnlyList<ChatUser> Participants,
    string? LastMessagePreview,
    DateTimeOffset? LastMessageAt,
    int UnreadCount,
    string? PhotoVersion = null,
    string Kind = "group",
    IReadOnlyDictionary<Guid, string>? GroupRoles = null,
    // Activity metadata survives personal/everyone message deletion. Keep it
    // separate from the currently visible message timestamp used for previews.
    DateTimeOffset? LastActivityAt = null,
    // True distinguishes an authoritative empty activity from an older server
    // that has not implemented this optional metadata yet.
    bool ActivityMetadataAvailable = false);
public sealed record GroupPhotoResult(Guid ConversationId, string? PhotoVersion);

public sealed record EncryptedPayload(
    string Algorithm,
    string EphemeralPublicKey,
    string Nonce,
    string Ciphertext,
    string Tag,
    string Signature,
    Guid RecipientId);

public sealed record SendMessageRequest(
    Guid ClientMessageId,
    Guid ConversationId,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    IReadOnlyList<EncryptedPayload> Payloads,
    EncryptedAttachment? Attachment);

public sealed record EncryptedAttachment(
    string FileName,
    string ContentType,
    long Size,
    string StorageToken,
    string Nonce,
    string Tag);

public sealed record StoredMessage(
    Guid Id,
    Guid ClientMessageId,
    Guid ConversationId,
    Guid SenderId,
    string Kind,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    bool DeletedForEveryone,
    IReadOnlyList<EncryptedPayload> Payloads,
    EncryptedAttachment? Attachment,
    MessageDelivery? Delivery = null,
    // Server acceptance time, not the client-signed CreatedAt, controls the deletion window.
    DateTimeOffset? DeleteForEveryoneUntil = null);

// Receipts are authenticated metadata, not message content. Only the sender receives this list.
public sealed record RecipientReceipt(Guid UserId, DateTimeOffset? DeliveredAt, DateTimeOffset? ReadAt);
public sealed record MessageDelivery(string Status, IReadOnlyList<RecipientReceipt> Recipients);
public sealed record AcknowledgeMessagesRequest(IReadOnlyList<Guid> MessageIds, bool Read = false);

public sealed record DeleteMessageRequest(bool ForEveryone);
public sealed record UploadTicket(string StorageToken, Uri UploadUri, DateTimeOffset ExpiresAt);
public sealed record ApiError(string Code, string Message);
