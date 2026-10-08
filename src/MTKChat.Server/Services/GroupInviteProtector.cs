using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace MTKChat.Server.Services;

public interface IGroupInviteProtector
{
    string Protect(Guid conversationId, Guid inviteId, string token);
    string? TryUnprotect(Guid conversationId, Guid inviteId, string protectedToken);
}

// Invitation addresses are bearer capabilities, not conversation encryption keys.
// Keep their recoverable values out of the database in plaintext. Separate room/id
// purposes also reject ciphertext copied from another invitation record.
public sealed class DataProtectionGroupInviteProtector(IDataProtectionProvider provider) : IGroupInviteProtector
{
    private IDataProtector For(Guid conversationId, Guid inviteId) => provider.CreateProtector(
        "MTKChat.GroupInvite.v1", conversationId.ToString("D"), inviteId.ToString("D"));

    public string Protect(Guid conversationId, Guid inviteId, string token) =>
        For(conversationId, inviteId).Protect(token);

    public string? TryUnprotect(Guid conversationId, Guid inviteId, string protectedToken)
    {
        try { return For(conversationId, inviteId).Unprotect(protectedToken); }
        catch (CryptographicException) { return null; }
        catch (FormatException) { return null; }
    }
}

public static class InviteProtectionKeyRing
{
    public static DirectoryInfo Prepare(string path)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(path));
        if (directory.LinkTarget is not null)
            throw new InvalidOperationException("Davet anahtar dizini sembolik bağlantı olamaz.");
        if (OperatingSystem.IsWindows()) directory.Create();
        else
        {
            Directory.CreateDirectory(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            // Linux Data Protection keys are OS-protected, not certificate-wrapped.
            // Reject an existing shared directory instead of silently weakening access.
            var mode = File.GetUnixFileMode(directory.FullName);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidOperationException("Davet anahtar dizini yalnızca servis hesabına açık olmalıdır (0700).");
        }
        return directory;
    }
}
