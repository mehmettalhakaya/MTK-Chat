using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class EncryptedFileTests
{
    [Fact]
    public void FileCipherRejectsTamperingAndRebindingAndNamesAreSanitized()
    {
        var client = Guid.NewGuid(); var room = Guid.NewGuid(); var sender = Guid.NewGuid();
        var original = RandomNumberGenerator.GetBytes(6000);
        var file = FileCryptography.Encrypt(original, "../../notes.txt", client, room, sender);
        Assert.Equal("notes.txt", file.Descriptor.FileName); Assert.NotEqual(original, file.Ciphertext);
        Assert.Equal(original, FileCryptography.Decrypt(file.Ciphertext, file.Descriptor, client, room, sender));
        Assert.ThrowsAny<CryptographicException>(() => FileCryptography.Decrypt(file.Ciphertext, file.Descriptor, Guid.NewGuid(), room, sender));
        file.Ciphertext[19] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => FileCryptography.Decrypt(file.Ciphertext, file.Descriptor, client, room, sender));
        Assert.Throws<InvalidDataException>(() => FileCryptography.Encrypt([], "empty", client, room, sender));
        Assert.Throws<InvalidDataException>(() => FileCryptography.Encrypt(new byte[FileCryptography.MaxFileBytes + 1], "big", client, room, sender));
    }

    [Fact]
    public async Task CiphertextStorageAndHttpDownloadRequireAnAuthorizedEnvelopeAndDeleteRevokesAccess()
    {
        var s = new ChatState();
        ChatUser User(string n) => s.UpsertSiteUser(Guid.NewGuid(), n, n + "@test.invalid", "user");
        var a = User("A"); var b = User("B"); var outside = User("Outside");
        var group = s.CreateConversation(a.Id, new("Private", [b.Id]))!;
        using var da = DeviceIdentity.Create(); using var db = DeviceIdentity.Create();
        var id = Guid.NewGuid(); var at = DateTimeOffset.UtcNow;
        var plaintext = "Private contents of a file, NOT plain server storage."u8.ToArray();
        var file = FileCryptography.Encrypt(plaintext, "private.txt", id, group.Id, a.Id);
        Assert.Null(s.UploadEncryptedFile(outside.Id, group.Id, id, file.Ciphertext));
        var token = s.UploadEncryptedFile(a.Id, group.Id, id, file.Ciphertext)!;
        Assert.Null(s.DownloadEncryptedFile(b.Id, token)); // Upload is private until a signed message references it.
        var descriptor = file.Descriptor with { StorageToken = token };
        var info = JsonSerializer.SerializeToUtf8Bytes(descriptor);
        EncryptedPayload To(ChatUser u, DeviceIdentity d) => MessageCryptography.Encrypt(info, id, group.Id, a.Id, u.Id, at,
            d.ExportEncryptionPublicKey(), da.SigningKey);
        var request = new SendMessageRequest(id, group.Id, "file", at, null, [To(a, da), To(b, db)],
            new("", "application/octet-stream", file.Ciphertext.Length, token, "", ""));
        Assert.False(s.AddMessage(b.Id, request, out _)); // Uploaded ciphertext belongs to A, not B.
        Assert.False(s.AddMessage(a.Id, request with { Attachment = request.Attachment! with { FileName = "leaked.txt" } }, out _));
        Assert.True(s.AddMessage(a.Id, request, out var stored));
        Assert.True(s.AddMessage(a.Id, request, out var repeated)); Assert.Equal(stored!.Id, repeated!.Id);
        Assert.Equal(file.Ciphertext, s.DownloadEncryptedFile(b.Id, token));
        Assert.Null(s.DownloadEncryptedFile(outside.Id, token));
        var envelope = Assert.Single(Assert.Single(s.GetMessages(b.Id, group.Id, null)).Payloads);
        var metadata = MessageCryptography.Decrypt(envelope, id, group.Id, a.Id, at, db.EncryptionKey, da.ExportSigningPublicKey());
        var decoded = JsonSerializer.Deserialize<EncryptedFileDescriptor>(metadata)!;
        Assert.Equal(plaintext, FileCryptography.Decrypt(s.DownloadEncryptedFile(b.Id, token)!, decoded, id, group.Id, a.Id));

        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(s); builder.Services.AddSingleton(new ChatDatabase(Options.Create(new DatabaseOptions())));
        builder.Services.AddRateLimiter(o => o.AddPolicy("encrypted-file", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
        await using var app = builder.Build(); app.UseMiddleware<ChatAuthenticationMiddleware>(); app.UseRateLimiter(); app.MapEncryptedFileEndpoints();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"api/files/{token}")).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.CreateSession(outside.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/files/{token}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync($"api/conversations/{group.Id}/files/{Guid.NewGuid()}", new ByteArrayContent(file.Ciphertext))).StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.CreateSession(b.Id));
        using var downloaded = await http.GetAsync($"api/files/{token}"); downloaded.EnsureSuccessStatusCode();
        Assert.Equal(file.Ciphertext, await downloaded.Content.ReadAsByteArrayAsync()); Assert.True(downloaded.Headers.CacheControl!.NoStore);
        s.SetBlocked(b.Id, a.Id, true); Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/files/{token}")).StatusCode);
        s.SetBlocked(b.Id, a.Id, false); s.DeleteMessage(b.Id, stored.Id, false);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"api/files/{token}")).StatusCode);
        Assert.NotNull(s.DownloadEncryptedFile(a.Id, token));
        s.DeleteMessage(a.Id, stored.Id, true); Assert.Null(s.DownloadEncryptedFile(a.Id, token));
        await app.StopAsync();
    }
}
