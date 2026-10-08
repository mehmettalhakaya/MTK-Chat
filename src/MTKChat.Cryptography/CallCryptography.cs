using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;

namespace MTKChat.Cryptography;

// Ephemeral keys are created per participation, signed by the existing device identity.
// The relay sees identities, timing and ciphertext, but never these private keys or PCM.
public sealed class CallIdentity : IDisposable
{
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public Guid CallId { get; }
    public Guid UserId { get; }
    public CallJoin Join { get; }
    public CallIdentity(Guid callId, Guid userId, ECDsa signer)
    {
        CallId = callId;
        UserId = userId;
        var unsigned = new CallJoin(Guid.NewGuid(), Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()), "");
        Join = unsigned with { Signature = Convert.ToBase64String(signer.SignData(Statement(callId, userId, unsigned), HashAlgorithmName.SHA256)) };
    }
    private static byte[] Statement(Guid call, Guid user, CallJoin join) =>
        Encoding.UTF8.GetBytes($"MTK-CALL-P256-V1|{call:N}|{user:N}|{join.SessionId:N}|{join.PublicKey}");

    public static void Verify(Guid call, Guid user, CallJoin join, string signingKey)
    {
        if (join.SessionId == Guid.Empty || join.PublicKey is null || join.Signature is null ||
            join.PublicKey.Length > 256 || join.Signature.Length > 128)
            throw new CryptographicException("Arama kimliği geçersiz.");
        using var signer = ECDsa.Create();
        signer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(signingKey), out _);
        if (!signer.VerifyData(Statement(call, user, join), Convert.FromBase64String(join.Signature), HashAlgorithmName.SHA256))
            throw new CryptographicException("Arama kimliği doğrulanamadı.");
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(join.PublicKey), out _);
        if (peer.ExportParameters(false).Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
            throw new CryptographicException("Desteklenmeyen arama anahtarı.");
    }

    public CallCipher Connect(Guid peerId, CallJoin peer, string signingKey)
    {
        Verify(CallId, peerId, peer, signingKey);
        using var remote = ECDiffieHellman.Create();
        remote.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peer.PublicKey), out _);
        var secret = _key.DeriveKeyMaterial(remote.PublicKey);
        try { return new CallCipher(CallId, UserId, Join, peerId, peer, secret); }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }
    public void Dispose() => _key.Dispose();
}

public sealed class CallCipher : IDisposable
{
    private readonly Guid _call, _self, _peer, _selfSession, _peerSession;
    private readonly byte[] _sendKey, _receiveKey;
    private long _sent, _received = -1;
    public string VerificationCode { get; }
    internal CallCipher(Guid call, Guid self, CallJoin own, Guid peer, CallJoin other, byte[] secret)
    {
        _call = call; _self = self; _peer = peer; _selfSession = own.SessionId; _peerSession = other.SessionId;
        _sendKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, call.ToByteArray(), Context(self, own.SessionId, peer, other.SessionId));
        _receiveKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, call.ToByteArray(), Context(peer, other.SessionId, self, own.SessionId));
        var ordered = new[] { $"{self:N}:{own.PublicKey}", $"{peer:N}:{other.PublicKey}" }.Order(StringComparer.Ordinal);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{call:N}|" + string.Join("|", ordered)));
        var code = Convert.ToHexString(hash.AsSpan(0, 12));
        VerificationCode = string.Join(" ", Enumerable.Range(0, 6).Select(i => code.Substring(i * 4, 4)));
    }
    private static byte[] Context(Guid sender, Guid source, Guid recipient, Guid target) =>
        Encoding.UTF8.GetBytes($"MTK-CALL-AES256GCM-V1|{sender:N}|{source:N}|{recipient:N}|{target:N}");
    private byte[] Aad(CallFrame frame) => Encoding.UTF8.GetBytes(FormattableString.Invariant(
        $"MTK-CALL-PCM16-16000-MONO-V1|{_call:N}|{frame.SenderId:N}|{frame.SenderSession:N}|{frame.RecipientId:N}|{frame.RecipientSession:N}|{frame.Sequence}"));
    private static byte[] Nonce(long sequence)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(4), sequence);
        return nonce;
    }
    public CallFrame Encrypt(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length is < 2 or > 6400 || pcm.Length % 2 != 0) throw new ArgumentException("Ses paketi boyutu geçersiz.");
        var sequence = checked(_sent++);
        var frame = new CallFrame(_self, _selfSession, _peer, _peerSession, sequence, "", "");
        var ciphertext = new byte[pcm.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_sendKey, 16);
        aes.Encrypt(Nonce(sequence), pcm, ciphertext, tag, Aad(frame));
        return frame with { Ciphertext = Convert.ToBase64String(ciphertext), Tag = Convert.ToBase64String(tag) };
    }
    public byte[] Decrypt(CallFrame frame)
    {
        if (frame.SenderId != _peer || frame.RecipientId != _self || frame.SenderSession != _peerSession ||
            frame.RecipientSession != _selfSession || frame.Sequence < 0 || frame.Sequence <= _received ||
            frame.Ciphertext.Length > 8536 || frame.Tag.Length != 24)
            throw new CryptographicException("Eski veya geçersiz ses paketi.");
        var encrypted = Convert.FromBase64String(frame.Ciphertext);
        if (encrypted.Length is < 2 or > 6400 || encrypted.Length % 2 != 0) throw new CryptographicException("Ses paketi boyutu geçersiz.");
        var pcm = new byte[encrypted.Length];
        try
        {
            using var aes = new AesGcm(_receiveKey, 16);
            aes.Decrypt(Nonce(frame.Sequence), encrypted, Convert.FromBase64String(frame.Tag), pcm, Aad(frame));
            _received = frame.Sequence; // A failed tag must never advance the replay cursor.
            return pcm;
        }
        catch { CryptographicOperations.ZeroMemory(pcm); throw; }
    }
    public void Dispose() { CryptographicOperations.ZeroMemory(_sendKey); CryptographicOperations.ZeroMemory(_receiveKey); }
}
