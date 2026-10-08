using Microsoft.AspNetCore.DataProtection;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class GroupInviteProtectorTests
{
    [Fact]
    public void TokenIsOpaqueAndBoundToItsRoomAndInvitation()
    {
        var protector = new DataProtectionGroupInviteProtector(new EphemeralDataProtectionProvider());
        var room = Guid.NewGuid(); var id = Guid.NewGuid(); var token = new string('A', 43);
        var encrypted = protector.Protect(room, id, token);
        Assert.DoesNotContain(token, encrypted);
        Assert.Equal(token, protector.TryUnprotect(room, id, encrypted));
        Assert.Null(protector.TryUnprotect(Guid.NewGuid(), id, encrypted));
        Assert.Null(protector.TryUnprotect(room, Guid.NewGuid(), encrypted));
        Assert.Null(protector.TryUnprotect(room, id, encrypted[..^7] + "invalid"));
        Assert.Null(protector.TryUnprotect(room, id, "invalid"));
        var other = new DataProtectionGroupInviteProtector(new EphemeralDataProtectionProvider());
        Assert.Null(other.TryUnprotect(room, id, encrypted));
    }

    [Fact]
    public void SeparateProvidersCanRecoverWithTheSamePrivatePersistentKeyRing()
    {
        var path = Path.Combine(Path.GetTempPath(), "MTKChat-invite-key-test-" + Guid.NewGuid().ToString("N"));
        var directory = InviteProtectionKeyRing.Prepare(path);
        var room = Guid.NewGuid(); var id = Guid.NewGuid(); var token = new string('B', 43);
        var first = new DataProtectionGroupInviteProtector(DataProtectionProvider.Create(directory,
            builder => builder.SetApplicationName("MTKChat.GroupInvites")));
        var encrypted = first.Protect(room, id, token);
        Assert.NotEmpty(directory.GetFiles("*.xml"));
        var second = new DataProtectionGroupInviteProtector(DataProtectionProvider.Create(directory,
            builder => builder.SetApplicationName("MTKChat.GroupInvites")));
        Assert.Equal(token, second.TryUnprotect(room, id, encrypted));
        var wrongApp = new DataProtectionGroupInviteProtector(DataProtectionProvider.Create(directory,
            builder => builder.SetApplicationName("different-app")));
        Assert.Null(wrongApp.TryUnprotect(room, id, encrypted));
        // Synthetic key files are intentionally left in this uniquely named temp
        // directory; no application or production key directory is touched.
    }
}
