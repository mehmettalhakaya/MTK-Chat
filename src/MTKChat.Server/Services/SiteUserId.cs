using System.Security.Cryptography;
using System.Text;

namespace MTKChat.Server.Services;

public static class SiteUserId
{
    // Keep the existing deterministic mapping so site users retain their chat identities.
    public static Guid FromExternalId(string externalId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"mtkaya.me:{externalId}"));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }
}
