namespace MTKChat.Contracts;

// Statuses use a dedicated cryptographic scope, never an existing conversation id.
// The server stores one shared encrypted body; each explicitly chosen audience
// member receives only their own signed envelope containing its AES descriptor.
public static class StatusProtocol
{
    public static readonly Guid ScopeId = Guid.Parse("d5555555-5555-4555-8555-555555555555");
    public const int MaximumBodyBytes = 512 * 1024;
    public const int MaximumEnvelopeBytes = 8 * 1024;
    public const int MaximumRecipients = 51; // Up to 50 selected people and the author.
    public const int MaximumActivePerSender = 5;
    public const int MaximumActiveStatuses = 200;
    public const int MaximumStoredBodyBytes = 32 * 1024 * 1024;
    public static bool IsKind(string? kind) => kind is "text" or "image/jpeg" or "image/png";
}

public sealed record SendStatusRequest(Guid ClientStatusId, string Kind, DateTimeOffset CreatedAt,
    string Ciphertext, IReadOnlyList<EncryptedPayload> Payloads);

// Feed rows contain no body, envelope, real names, e-mail, or audience list.
public sealed record StatusSummary(Guid Id, Guid ClientStatusId, Guid SenderId, string Kind,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record StoredStatus(Guid Id, Guid ClientStatusId, Guid ScopeId, Guid SenderId, string Kind,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Ciphertext, EncryptedPayload Payload);
