using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;

namespace MTKChat.Cryptography;

public static class FileCryptography
{
    public const int MaxFileBytes = 5 * 1024 * 1024;
    private static byte[] AssociatedData(Guid clientId, Guid roomId, Guid senderId) =>
        Encoding.UTF8.GetBytes($"MTK-file-v1:{clientId:N}:{roomId:N}:{senderId:N}");

    // The random file key and real filename travel inside the signed, recipient-specific
    // message envelope. The storage server receives only ciphertext and a random token.
    public static (byte[] Ciphertext, EncryptedFileDescriptor Descriptor) Encrypt(byte[] plaintext,
        string name, Guid clientId, Guid roomId, Guid senderId)
    {
        if (plaintext.Length is < 1 or > MaxFileBytes) throw new InvalidDataException("Dosya 1 bayt–5 MB olmalı.");
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(clientId, roomId, senderId));
            return (ciphertext, new EncryptedFileDescriptor(SafeName(name), "application/octet-stream", plaintext.Length,
                "", Convert.ToBase64String(key), Convert.ToBase64String(nonce), Convert.ToBase64String(tag)));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static byte[] Decrypt(byte[] ciphertext, EncryptedFileDescriptor descriptor,
        Guid clientId, Guid roomId, Guid senderId)
    {
        if (descriptor.Size is < 1 or > MaxFileBytes || ciphertext.LongLength != descriptor.Size)
            throw new InvalidDataException("Şifreli dosya boyutu uyuşmuyor.");
        var key = Convert.FromBase64String(descriptor.Key);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            if (key.Length != 32) throw new CryptographicException("Dosya anahtarı geçersiz.");
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(descriptor.Nonce), ciphertext, Convert.FromBase64String(descriptor.Tag),
                plaintext, AssociatedData(clientId, roomId, senderId));
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static string SafeName(string name)
    {
        var leaf = name.Replace('\\', '/').Split('/').LastOrDefault() ?? "dosya";
        var safe = new string(leaf.Where(c => !char.IsControl(c) && !"<>:\"/\\|?*".Contains(c)).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(safe) ? "dosya" : safe[..Math.Min(safe.Length, 180)];
    }
}
