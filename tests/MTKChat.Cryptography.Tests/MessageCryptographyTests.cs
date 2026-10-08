using System.Security.Cryptography;
using System.Text;
using MTKChat.Cryptography;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class MessageCryptographyTests
{
    [Fact]
    public void WaveVoiceBytesRoundTripThroughRecipientEnvelope()
    {
        using var sender = DeviceIdentity.Create();
        using var recipient = DeviceIdentity.Create();
        var bytes = new byte[44 + 32000];
        "RIFF"u8.CopyTo(bytes);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));
        RandomNumberGenerator.Fill(bytes.AsSpan(44));
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var envelope = MessageCryptography.Encrypt(bytes, messageId, conversationId, senderId, Guid.NewGuid(),
            createdAt, recipient.ExportEncryptionPublicKey(), sender.SigningKey);
        Assert.Equal(bytes, MessageCryptography.Decrypt(envelope, messageId, conversationId, senderId,
            createdAt, recipient.EncryptionKey, sender.ExportSigningPublicKey()));
        Assert.NotEqual(Convert.ToBase64String(bytes), envelope.Ciphertext);
    }
    [Fact]
    public void EncryptDecrypt_RoundTripsAndAuthenticates()
    {
        using var sender = DeviceIdentity.Create();
        using var recipient = DeviceIdentity.Create();
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var plaintext = Encoding.UTF8.GetBytes("merhaba 🔒");

        var encrypted = MessageCryptography.Encrypt(
            plaintext,
            messageId,
            conversationId,
            senderId,
            recipientId,
            createdAt,
            recipient.ExportEncryptionPublicKey(),
            sender.SigningKey);

        var decrypted = MessageCryptography.Decrypt(
            encrypted,
            messageId,
            conversationId,
            senderId,
            createdAt,
            recipient.EncryptionKey,
            sender.ExportSigningPublicKey());

        Assert.Equal(plaintext, decrypted);
        Assert.NotEqual(Convert.ToBase64String(plaintext), encrypted.Ciphertext);
    }

    [Fact]
    public void Decrypt_RejectsTamperedCiphertext()
    {
        using var sender = DeviceIdentity.Create();
        using var recipient = DeviceIdentity.Create();
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var encrypted = MessageCryptography.Encrypt(
            "secret"u8,
            messageId,
            conversationId,
            senderId,
            recipientId,
            createdAt,
            recipient.ExportEncryptionPublicKey(),
            sender.SigningKey);
        var bytes = Convert.FromBase64String(encrypted.Ciphertext);
        bytes[0] ^= 0x01;
        var tampered = encrypted with { Ciphertext = Convert.ToBase64String(bytes) };

        Assert.ThrowsAny<CryptographicException>(() => MessageCryptography.Decrypt(
            tampered,
            messageId,
            conversationId,
            senderId,
            createdAt,
            recipient.EncryptionKey,
            sender.ExportSigningPublicKey()));
    }

    [Fact]
    public void Decrypt_RejectsWrongRecipient()
    {
        using var sender = DeviceIdentity.Create();
        using var recipient = DeviceIdentity.Create();
        using var stranger = DeviceIdentity.Create();
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var encrypted = MessageCryptography.Encrypt(
            "secret"u8,
            messageId,
            conversationId,
            senderId,
            recipientId,
            createdAt,
            recipient.ExportEncryptionPublicKey(),
            sender.SigningKey);

        Assert.ThrowsAny<CryptographicException>(() => MessageCryptography.Decrypt(
            encrypted,
            messageId,
            conversationId,
            senderId,
            createdAt,
            stranger.EncryptionKey,
            sender.ExportSigningPublicKey()));
    }

    [Fact]
    public void VerifyEnvelope_RejectsTamperedMetadataBeforeStorage()
    {
        using var sender = DeviceIdentity.Create();
        using var recipient = DeviceIdentity.Create();
        var messageId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var senderId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        var encrypted = MessageCryptography.Encrypt("secret"u8, messageId, conversationId, senderId,
            recipientId, createdAt, recipient.ExportEncryptionPublicKey(), sender.SigningKey);

        Assert.ThrowsAny<CryptographicException>(() => MessageCryptography.VerifyEnvelope(
            encrypted, messageId, Guid.NewGuid(), senderId, createdAt, sender.ExportSigningPublicKey()));
    }
}
