using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Server.Configuration;

namespace MTKChat.Server.Services;

public sealed class SiteAuthenticationService(HttpClient http, IOptions<SiteAuthenticationOptions> options)
{
    public async Task<SiteIdentity?> AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.BaseUrl) || string.IsNullOrWhiteSpace(settings.VerifyPath))
            throw new InvalidOperationException("mtkaya.me kimlik doğrulama adresi yapılandırılmadı.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), settings.VerifyPath))
        {
            Content = JsonContent.Create(new { login = request.Email, password = request.Password })
        };
        using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.BadRequest)
            return null;
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<SiteLoginResponse>(cancellationToken: cancellationToken);
        if (result?.User is null || string.IsNullOrWhiteSpace(result.Token) || string.IsNullOrWhiteSpace(result.User.Email))
            throw new InvalidOperationException("mtkaya.me beklenmeyen bir giriş yanıtı döndürdü.");

        var externalId = result.User.Id.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? string.Empty
            : result.User.Id.ToString();
        var stableSource = string.IsNullOrWhiteSpace(externalId) ? result.User.Email.Trim().ToLowerInvariant() : externalId;
        return new SiteIdentity(
            SiteUserId.FromExternalId(stableSource),
            string.IsNullOrWhiteSpace(result.User.Username) ? result.User.Email : result.User.Username,
            result.User.Email,
            string.Equals(result.User.Role, "admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "user");
    }

    private sealed record SiteLoginResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("user")] SiteUser User);

    private sealed record SiteUser(
        [property: JsonPropertyName("id")] JsonElement Id,
        [property: JsonPropertyName("username")] string Username,
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string? Role);
}

public sealed record SiteIdentity(Guid Id, string DisplayName, string Email, string Role);
