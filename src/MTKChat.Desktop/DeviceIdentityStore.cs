using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MTKChat.Cryptography;

namespace MTKChat.Desktop;

internal static class DeviceIdentityStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("MTKChat.DeviceIdentity.v1");
    private static readonly string KeyFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MTKChat",
        "device-identity.json");

    public static DeviceIdentity LoadOrCreate()
    {
        if (File.Exists(KeyFile))
        {
            var protectedKeys = JsonSerializer.Deserialize<ProtectedKeys>(File.ReadAllText(KeyFile))
                ?? throw new CryptographicException("Cihaz anahtar dosyası okunamadı.");
            var encryption = ProtectedData.Unprotect(Convert.FromBase64String(protectedKeys.Encryption), Entropy, DataProtectionScope.CurrentUser);
            var signing = ProtectedData.Unprotect(Convert.FromBase64String(protectedKeys.Signing), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                return DeviceIdentity.Import(encryption, signing);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encryption);
                CryptographicOperations.ZeroMemory(signing);
            }
        }

        var identity = DeviceIdentity.Create();
        var directory = Path.GetDirectoryName(KeyFile)!;
        Directory.CreateDirectory(directory);
        var encryptionPrivateBytes = identity.ExportEncryptionPrivateKey();
        var signingPrivateBytes = identity.ExportSigningPrivateKey();
        try
        {
            var data = new ProtectedKeys(
                Convert.ToBase64String(ProtectedData.Protect(encryptionPrivateBytes, Entropy, DataProtectionScope.CurrentUser)),
                Convert.ToBase64String(ProtectedData.Protect(signingPrivateBytes, Entropy, DataProtectionScope.CurrentUser)));
            File.WriteAllText(KeyFile, JsonSerializer.Serialize(data));
            return identity;
        }
        catch
        {
            identity.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptionPrivateBytes);
            CryptographicOperations.ZeroMemory(signingPrivateBytes);
        }
    }

    private sealed record ProtectedKeys(string Encryption, string Signing);
}
