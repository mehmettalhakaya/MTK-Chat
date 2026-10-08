using System.Security.Cryptography;
using System.Text;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class StatusCryptographyTests
{
    private static DeviceKeyBundle Bundle(Guid id, DeviceIdentity identity) => new(id, Guid.NewGuid(),
        identity.ExportEncryptionPublicKey(), identity.ExportSigningPublicKey());
    private static StoredStatus Stored(SendStatusRequest request, Guid sender, Guid recipient) => new(Guid.NewGuid(),
        request.ClientStatusId, StatusProtocol.ScopeId, sender, request.Kind, request.CreatedAt,
        request.CreatedAt.AddDays(1), request.Ciphertext, request.Payloads.Single(p => p.RecipientId == recipient));

    [Theory]
    [InlineData("text")]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    public void Shared_body_decrypts_only_for_explicit_recipients(string kind)
    {
        using var author = DeviceIdentity.Create(); using var reader = DeviceIdentity.Create(); using var stranger = DeviceIdentity.Create();
        var owner = Guid.NewGuid(); var viewer = Guid.NewGuid(); var content = Encoding.UTF8.GetBytes("Özel durum 🔒");
        var request = StatusCryptography.Encrypt(content, kind, Guid.NewGuid(), owner, DateTimeOffset.UtcNow,
            [Bundle(owner, author), Bundle(viewer, reader)], author.SigningKey);
        Assert.Equal(2, request.Payloads.Count);
        Assert.True(request.Payloads.All(p => Convert.FromBase64String(p.Ciphertext).Length < 1024));
        var status = Stored(request, owner, viewer);
        Assert.Equal(content, StatusCryptography.Decrypt(status, viewer, reader.EncryptionKey, author.ExportSigningPublicKey()));
        Assert.Equal(content, StatusCryptography.Decrypt(Stored(request, owner, owner), owner, author.EncryptionKey, author.ExportSigningPublicKey()));
        Assert.ThrowsAny<CryptographicException>(() => StatusCryptography.Decrypt(status, viewer, stranger.EncryptionKey, author.ExportSigningPublicKey()));
        Assert.ThrowsAny<CryptographicException>(() => StatusCryptography.Decrypt(status, Guid.NewGuid(), reader.EncryptionKey, author.ExportSigningPublicKey()));
    }

    [Fact]
    public void Swapping_body_kind_scope_or_client_id_cannot_create_a_valid_status()
    {
        using var author = DeviceIdentity.Create(); using var reader = DeviceIdentity.Create();
        var owner = Guid.NewGuid(); var viewer = Guid.NewGuid();
        var request = StatusCryptography.Encrypt([1, 2, 3], "image/jpeg", Guid.NewGuid(), owner, DateTimeOffset.UtcNow,
            [Bundle(owner, author), Bundle(viewer, reader)], author.SigningKey);
        var status = Stored(request, owner, viewer);
        foreach (var tampered in new[] { status with { Kind = "text" }, status with { ScopeId = Guid.NewGuid() },
                     status with { ClientStatusId = Guid.NewGuid() }, status with { SenderId = Guid.NewGuid() },
                     status with { Ciphertext = Convert.ToBase64String([4, 5, 6]) } })
            Assert.ThrowsAny<CryptographicException>(() => StatusCryptography.Decrypt(tampered, viewer, reader.EncryptionKey, author.ExportSigningPublicKey()));
    }

    [Fact]
    public void Images_are_not_duplicated_in_recipient_envelopes()
    {
        using var author = DeviceIdentity.Create(); using var reader = DeviceIdentity.Create();
        var owner = Guid.NewGuid(); var viewer = Guid.NewGuid(); var content = RandomNumberGenerator.GetBytes(StatusProtocol.MaximumBodyBytes);
        var request = StatusCryptography.Encrypt(content, "image/jpeg", Guid.NewGuid(), owner, DateTimeOffset.UtcNow,
            [Bundle(owner, author), Bundle(viewer, reader)], author.SigningKey);
        Assert.Equal(content.Length, Convert.FromBase64String(request.Ciphertext).Length);
        Assert.All(request.Payloads, p => Assert.InRange(Convert.FromBase64String(p.Ciphertext).Length, 1, 1024));
        Assert.Equal(content, StatusCryptography.Decrypt(Stored(request, owner, viewer), viewer, reader.EncryptionKey, author.ExportSigningPublicKey()));
    }

    [Fact]
    public void Reject_empty_oversize_and_implicit_audience()
    {
        using var author = DeviceIdentity.Create(); using var reader = DeviceIdentity.Create();
        var owner = Guid.NewGuid(); var self = Bundle(owner, author); var viewer = Bundle(Guid.NewGuid(), reader);
        foreach (var content in new[] { Array.Empty<byte>(), new byte[StatusProtocol.MaximumBodyBytes + 1] })
            Assert.Throws<InvalidDataException>(() => StatusCryptography.Encrypt(content, "text", Guid.NewGuid(), owner, DateTimeOffset.UtcNow, [self, viewer], author.SigningKey));
        foreach (var audience in new DeviceKeyBundle[][] { [self], [viewer, viewer], [self, viewer with { UserId = Guid.Empty }] })
            Assert.Throws<InvalidDataException>(() => StatusCryptography.Encrypt([1], "text", Guid.NewGuid(), owner, DateTimeOffset.UtcNow, audience, author.SigningKey));
    }
}
