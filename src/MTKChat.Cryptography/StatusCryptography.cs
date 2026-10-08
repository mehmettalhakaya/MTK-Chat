using System.Security.Cryptography;
using System.Text.Json;
using MTKChat.Contracts;

namespace MTKChat.Cryptography;

// A picture is encrypted once, not once per viewer. Only its small random-key
// descriptor is placed inside each authenticated recipient-specific envelope.
public static class StatusCryptography
{
    public const int MaximumContentBytes = 512 * 1024;

    public static SendStatusRequest Encrypt(byte[] content, string kind, Guid clientId, Guid sender,
        DateTimeOffset createdAt, IReadOnlyList<DeviceKeyBundle> recipients, ECDsa signingKey)
    {
        if (content.Length is < 1 or > MaximumContentBytes || kind is not ("text" or "image/jpeg" or "image/png") ||
            clientId == Guid.Empty || sender == Guid.Empty || createdAt == default || recipients.Count is < 2 or > 51 ||
            recipients.Any(d => d is null || d.UserId == Guid.Empty) ||
            recipients.Select(d => d.UserId).Distinct().Count() != recipients.Count ||
            !recipients.Any(d => d.UserId == sender))
            throw new InvalidDataException("Durum içeriği veya seçilen kişiler geçersiz.");
        var encrypted = FileCryptography.Encrypt(content, "status", clientId, StatusProtocol.ScopeId, sender);
        var descriptor = encrypted.Descriptor with { ContentType = kind };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        try
        {
            var envelopes = recipients.Select(d => MessageCryptography.Encrypt(bytes, clientId,
                StatusProtocol.ScopeId, sender, d.UserId, createdAt, d.EncryptionPublicKey, signingKey)).ToArray();
            return new SendStatusRequest(clientId, kind, createdAt, Convert.ToBase64String(encrypted.Ciphertext), envelopes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); CryptographicOperations.ZeroMemory(encrypted.Ciphertext); }
    }

    public static byte[] Decrypt(StoredStatus status, Guid viewer, ECDiffieHellman privateKey, string senderSigningKey)
    {
        if (status.Id == Guid.Empty || status.ClientStatusId == Guid.Empty || status.SenderId == Guid.Empty || viewer == Guid.Empty ||
            status.Payload is null || status.Ciphertext is null || status.Payload.Ciphertext is null ||
            status.Payload.Ciphertext.Length > (StatusProtocol.MaximumEnvelopeBytes + 2) / 3 * 4 ||
            status.ScopeId != StatusProtocol.ScopeId || status.Payload.RecipientId != viewer ||
            status.Kind is not ("text" or "image/jpeg" or "image/png") ||
            status.Ciphertext.Length > (MaximumContentBytes + 2) / 3 * 4)
            throw new CryptographicException("Durum zarfı geçersiz.");
        var descriptorBytes = MessageCryptography.Decrypt(status.Payload, status.ClientStatusId,
            status.ScopeId, status.SenderId, status.CreatedAt, privateKey, senderSigningKey);
        try
        {
            var descriptor = JsonSerializer.Deserialize<EncryptedFileDescriptor>(descriptorBytes)
                ?? throw new CryptographicException("Durum anahtarı bulunamadı.");
            if (descriptor.ContentType != status.Kind || descriptor.Size is < 1 or > MaximumContentBytes)
                throw new CryptographicException("Durum türü veya boyutu değiştirildi.");
            var cipher = Convert.FromBase64String(status.Ciphertext);
            try { return FileCryptography.Decrypt(cipher, descriptor, status.ClientStatusId, status.ScopeId, status.SenderId); }
            finally { CryptographicOperations.ZeroMemory(cipher); }
        }
        finally { CryptographicOperations.ZeroMemory(descriptorBytes); }
    }
}
