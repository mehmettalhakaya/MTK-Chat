using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;

namespace MTKChat.Cryptography;

public static class MessageCryptography
{
    public const string Algorithm = "MTK-ECDH-P256-HKDF-SHA256-AES256GCM-v1";

    public static EncryptedPayload Encrypt(
        ReadOnlySpan<byte> plaintext,
        Guid messageId,
        Guid conversationId,
        Guid senderId,
        Guid recipientId,
        DateTimeOffset createdAt,
        string recipientEncryptionPublicKey,
        ECDsa senderSigningKey)
    {
        using var recipientKey = ECDiffieHellman.Create();
        recipientKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(recipientEncryptionPublicKey), out _);
        using var ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

        var sharedSecret = ephemeralKey.DeriveKeyMaterial(recipientKey.PublicKey);
        var salt = messageId.ToByteArray();
        var info = BuildInfo(conversationId, senderId, recipientId, createdAt);
        byte[] key;
        try { key = HkdfSha256(sharedSecret, salt, info, 32); }
        finally { CryptographicOperations.ZeroMemory(sharedSecret); }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        var ephemeralPublicKey = ephemeralKey.ExportSubjectPublicKeyInfo();
        var aad = BuildAad(messageId, conversationId, senderId, recipientId, createdAt, ephemeralPublicKey);

        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
        }
        finally { CryptographicOperations.ZeroMemory(key); }

        var signatureInput = Combine(aad, nonce, ciphertext, tag);
        var signature = senderSigningKey.SignData(signatureInput, HashAlgorithmName.SHA256);

        return new EncryptedPayload(
            Algorithm,
            Convert.ToBase64String(ephemeralPublicKey),
            Convert.ToBase64String(nonce),
            Convert.ToBase64String(ciphertext),
            Convert.ToBase64String(tag),
            Convert.ToBase64String(signature),
            recipientId);
    }

    public static byte[] Decrypt(
        EncryptedPayload payload,
        Guid messageId,
        Guid conversationId,
        Guid senderId,
        DateTimeOffset createdAt,
        ECDiffieHellman recipientPrivateKey,
        string senderSigningPublicKey)
    {
        VerifyEnvelope(payload, messageId, conversationId, senderId, createdAt, senderSigningPublicKey);

        var ephemeralBytes = Convert.FromBase64String(payload.EphemeralPublicKey);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        var tag = Convert.FromBase64String(payload.Tag);
        var aad = BuildAad(messageId, conversationId, senderId, payload.RecipientId, createdAt, ephemeralBytes);

        using var ephemeralKey = ECDiffieHellman.Create();
        ephemeralKey.ImportSubjectPublicKeyInfo(ephemeralBytes, out _);
        var sharedSecret = recipientPrivateKey.DeriveKeyMaterial(ephemeralKey.PublicKey);
        byte[] key;
        try
        {
            key = HkdfSha256(sharedSecret, messageId.ToByteArray(),
                BuildInfo(conversationId, senderId, payload.RecipientId, createdAt), 32);
        }
        finally { CryptographicOperations.ZeroMemory(sharedSecret); }

        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
        return plaintext;
    }

    public static void VerifyEnvelope(
        EncryptedPayload payload,
        Guid messageId,
        Guid conversationId,
        Guid senderId,
        DateTimeOffset createdAt,
        string senderSigningPublicKey)
    {
        if (!string.Equals(payload.Algorithm, Algorithm, StringComparison.Ordinal))
            throw new CryptographicException("Desteklenmeyen şifreleme algoritması.");

        var ephemeralBytes = Convert.FromBase64String(payload.EphemeralPublicKey);
        var nonce = Convert.FromBase64String(payload.Nonce);
        var ciphertext = Convert.FromBase64String(payload.Ciphertext);
        var tag = Convert.FromBase64String(payload.Tag);
        var signature = Convert.FromBase64String(payload.Signature);
        if (nonce.Length != 12 || tag.Length != 16)
            throw new CryptographicException("Mesaj zarfı geçersiz.");

        var aad = BuildAad(messageId, conversationId, senderId, payload.RecipientId, createdAt, ephemeralBytes);
        using var signingKey = ECDsa.Create();
        signingKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(senderSigningPublicKey), out _);
        if (!signingKey.VerifyData(Combine(aad, nonce, ciphertext, tag), signature, HashAlgorithmName.SHA256))
            throw new CryptographicException("Mesaj imzası doğrulanamadı.");
    }

    private static byte[] BuildInfo(Guid conversationId, Guid senderId, Guid recipientId, DateTimeOffset createdAt) =>
        Encoding.UTF8.GetBytes($"{Algorithm}|{conversationId:N}|{senderId:N}|{recipientId:N}|{createdAt.ToUnixTimeMilliseconds()}");

    private static byte[] BuildAad(Guid messageId, Guid conversationId, Guid senderId, Guid recipientId, DateTimeOffset createdAt, byte[] ephemeralPublicKey) =>
        Combine(
            Encoding.UTF8.GetBytes(Algorithm),
            messageId.ToByteArray(),
            conversationId.ToByteArray(),
            senderId.ToByteArray(),
            recipientId.ToByteArray(),
            BitConverter.GetBytes(createdAt.ToUnixTimeMilliseconds()),
            ephemeralPublicKey);

    private static byte[] HkdfSha256(byte[] ikm, byte[] salt, byte[] info, int length)
    {
        using var extract = new HMACSHA256(salt);
        var prk = extract.ComputeHash(ikm);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;

        while (offset < length)
        {
            using var expand = new HMACSHA256(prk);
            var blockInput = Combine(previous, info, new[] { counter });
            previous = expand.ComputeHash(blockInput);
            var count = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, count).CopyTo(output.AsSpan(offset));
            offset += count;
            counter++;
        }

        CryptographicOperations.ZeroMemory(prk);
        return output;
    }

    private static byte[] Combine(params byte[][] arrays)
    {
        var length = arrays.Sum(static item => item.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var item in arrays)
        {
            item.CopyTo(result, offset);
            offset += item.Length;
        }
        return result;
    }
}
