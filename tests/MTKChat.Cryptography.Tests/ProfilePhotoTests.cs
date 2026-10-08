using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MTKChat.Contracts;
using MTKChat.Server.Services;
using SkiaSharp;
using Xunit;

namespace MTKChat.Cryptography.Tests;

public sealed class ProfilePhotoTests
{
    private static byte[] Png(int width = 360, int height = 240)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void NormalizationProducesSmallJpegAndContentVersion()
    {
        var normalized = ProfilePhotoCodec.Normalize(Png());
        using var decoded = SKBitmap.Decode(normalized.Jpeg);
        Assert.Equal(256, decoded.Width);
        Assert.Equal(256, decoded.Height);
        Assert.Equal(0xff, normalized.Jpeg[0]);
        Assert.Equal(0xd8, normalized.Jpeg[1]);
        Assert.Equal(64, normalized.Version.Length);
        Assert.Equal(normalized.Version, ProfilePhotoCodec.Normalize(Png()).Version);
        Assert.True(normalized.Jpeg.Length < 200_000);
    }

    [Fact]
    public void RejectsEmptyNonImageOversizedAndDecompressionBomb()
    {
        Assert.Throws<InvalidDataException>(() => ProfilePhotoCodec.Normalize([]));
        Assert.Throws<InvalidDataException>(() => ProfilePhotoCodec.Normalize("<svg onload='alert(1)'/>"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => ProfilePhotoCodec.Normalize(new byte[ProfilePhotoCodec.MaxUploadBytes + 1]));
        Assert.Throws<InvalidDataException>(() => ProfilePhotoCodec.Normalize(Png(2100, 2000)));
    }

    [Fact]
    public void PhotoSurvivesAuthoritativeSiteRoleRefreshAndCanBeRemoved()
    {
        var state = new ChatState();
        var id = Guid.NewGuid();
        state.UpsertSiteUser(id, "test", "test@example.invalid", "user");
        var photo = ProfilePhotoCodec.Normalize(Png());
        state.SetProfilePhoto(id, photo);
        state.SynchronizeSiteUsers([new SiteAccount(9, id, "test", "test@example.invalid", "admin", true)]);
        Assert.Equal(photo.Version, state.GetUser(id)!.PhotoVersion);
        Assert.Equal("admin", state.GetUser(id)!.Role);
        Assert.Null(state.SetProfilePhoto(Guid.Parse("22222222-2222-2222-2222-222222222222"), photo));
        Assert.Null(state.SetProfilePhoto(Guid.NewGuid(), photo));
        Assert.Null(state.SetProfilePhoto(id, null)!.PhotoVersion);
        Assert.Null(state.GetProfilePhoto(id));
    }

    [Fact]
    public async Task ProfileRoutesUseSessionOwnerAndRoundTripNormalizedPhoto()
    {
        // Isolated HTTP host: no live accounts, database or model providers are contacted.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var state = new ChatState();
        var owner = state.UpsertSiteUser(Guid.NewGuid(), "owner", "owner@example.invalid", "user");
        var other = state.UpsertSiteUser(Guid.NewGuid(), "other", "other@example.invalid", "admin");
        var token = state.CreateSession(owner.Id);
        builder.Services.AddSingleton(state);
        builder.Services.AddRateLimiter(options => options.AddPolicy("profile-photo", _ =>
            System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var id = state.ResolveSession(context.Request.Headers.Authorization.ToString().Replace("Bearer ", ""));
            if (id is null) { context.Response.StatusCode = 401; return; }
            context.Items["UserId"] = id.Value;
            await next();
        });
        app.UseRateLimiter();
        app.MapProfileEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"api/users/{owner.Id}/photo")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var put = await client.PutAsync($"api/profile/photo?userId={other.Id}", new ByteArrayContent(Png()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await put.Content.ReadFromJsonAsync<ChatUser>();
        Assert.Equal(owner.Id, result!.Id);
        Assert.NotNull(result.PhotoVersion);
        Assert.Null(state.GetProfilePhoto(other.Id));
        using var downloaded = await client.GetAsync($"api/users/{owner.Id}/photo");
        Assert.Equal("image/jpeg", downloaded.Content.Headers.ContentType!.MediaType);
        Assert.True(downloaded.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsync("api/profile/photo", new ByteArrayContent(AnimatedPhotoTests.Gif()))).StatusCode);
        using var gifResponse = await client.GetAsync($"api/users/{owner.Id}/photo");
        Assert.Equal("image/gif", gifResponse.Content.Headers.ContentType!.MediaType);
        using var gifData = SKData.CreateCopy(await gifResponse.Content.ReadAsByteArrayAsync());
        using var gifCodec = SKCodec.Create(gifData);
        Assert.Equal(2, gifCodec.FrameCount);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("api/profile/photo", new ByteArrayContent([1, 2, 3]))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await client.PutAsync("api/profile/photo", new ByteArrayContent(new byte[ProfilePhotoCodec.MaxUploadBytes + 1]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("api/profile/photo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"api/users/{owner.Id}/photo")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public async Task GroupPhotoRoutesRequireMembershipAndKeepOtherGroupsUnchanged()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var state = new ChatState();
        var member = state.UpsertSiteUser(Guid.NewGuid(), "member", "member@example.invalid", "user");
        var outsider = state.UpsertSiteUser(Guid.NewGuid(), "outsider", "outsider@example.invalid", "user");
        var group = state.CreateConversation(member.Id, new CreateConversationRequest("Team", [Guid.Parse("22222222-2222-2222-2222-222222222222")]))!;
        var otherGroup = state.CreateConversation(outsider.Id, new CreateConversationRequest("Other", [Guid.Parse("33333333-3333-3333-3333-333333333333")]))!;
        var tokens = new Dictionary<string, Guid> { [state.CreateSession(member.Id)] = member.Id, [state.CreateSession(outsider.Id)] = outsider.Id };
        builder.Services.AddSingleton(state);
        builder.Services.AddRateLimiter(options => options.AddPolicy("profile-photo", _ =>
            System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("test")));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", "");
            if (!tokens.TryGetValue(token, out var id)) { context.Response.StatusCode = 401; return; }
            context.Items["UserId"] = id;
            await next();
        });
        app.UseRateLimiter();
        app.MapGroupPhotoEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var path = $"api/conversations/{group.Id}/photo";
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Single(kv => kv.Value == outsider.Id).Key);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsync(path, new ByteArrayContent(Png()))).StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Single(kv => kv.Value == member.Id).Key);
        using var put = await client.PutAsync(path, new ByteArrayContent(Png()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var result = await put.Content.ReadFromJsonAsync<GroupPhotoResult>();
        Assert.Equal(group.Id, result!.ConversationId);
        Assert.NotNull(result.PhotoVersion);
        Assert.Equal(result.PhotoVersion, state.GetConversations(member.Id).Single(item => item.Id == group.Id).PhotoVersion);
        Assert.Null(state.GetGroupPhoto(member.Id, otherGroup.Id));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync(path, new ByteArrayContent([1, 2]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        await app.StopAsync();
    }
}
