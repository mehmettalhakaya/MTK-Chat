using System.Security.Cryptography;

namespace MTKChat.Cryptography;

public sealed class DeviceIdentity : IDisposable
{
    public ECDiffieHellman EncryptionKey { get; }
    public ECDsa SigningKey { get; }

    private DeviceIdentity(ECDiffieHellman encryptionKey, ECDsa signingKey)
    {
        EncryptionKey = encryptionKey;
        SigningKey = signingKey;
    }

    public static DeviceIdentity Create() => new(
        ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256),
        ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static DeviceIdentity Import(byte[] encryptionPrivateKey, byte[] signingPrivateKey)
    {
        var encryption = ECDiffieHellman.Create();
        encryption.ImportPkcs8PrivateKey(encryptionPrivateKey, out _);
        var signing = ECDsa.Create();
        signing.ImportPkcs8PrivateKey(signingPrivateKey, out _);
        return new DeviceIdentity(encryption, signing);
    }

    public byte[] ExportEncryptionPrivateKey() => EncryptionKey.ExportPkcs8PrivateKey();
    public byte[] ExportSigningPrivateKey() => SigningKey.ExportPkcs8PrivateKey();
    public string ExportEncryptionPublicKey() => Convert.ToBase64String(EncryptionKey.ExportSubjectPublicKeyInfo());
    public string ExportSigningPublicKey() => Convert.ToBase64String(SigningKey.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        EncryptionKey.Dispose();
        SigningKey.Dispose();
    }
}
