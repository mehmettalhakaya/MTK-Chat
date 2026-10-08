using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using MTKChat.Contracts;
using MTKChat.Cryptography;
using MTKChat.Server.Agents;
using MTKChat.Server.Configuration;
using MTKChat.Server.Services;

var builder = WebApplication.CreateBuilder(args);
// Keep bearer invite fragments/body data out of request-path diagnostics even
// if production's shared logging settings later enable informational tracing.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.HttpLogging", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
builder.Services.AddSingleton<CallRegistry>();
if (builder.Environment.IsProduction())
    builder.Configuration.AddJsonFile("/etc/mtk-chat/agent-secrets.json", optional: false, reloadOnChange: false);
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<SiteAuthenticationOptions>(builder.Configuration.GetSection(SiteAuthenticationOptions.SectionName));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.AddSingleton<ChatDatabase>();
// This private directory is outside release checkouts, so package replacement
// preserves URL recovery. Back it up together with the chat snapshot, not the site.
var inviteKeyPath = builder.Configuration["InviteProtection:KeyRingPath"] ??
    (builder.Environment.IsProduction() && !OperatingSystem.IsWindows()
        ? "/etc/mtk-chat/invite-keys"
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MTKChat", "Server", "invite-keys"));
builder.Services.AddDataProtection().SetApplicationName("MTKChat.GroupInvites")
    .PersistKeysToFileSystem(InviteProtectionKeyRing.Prepare(inviteKeyPath));
builder.Services.AddSingleton<IGroupInviteProtector, DataProtectionGroupInviteProtector>();
builder.Services.AddSingleton<ChatState>();
builder.Services.AddHttpClient<SiteAuthenticationService>(client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<GeminiAgentProvider>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient<GroqAgentProvider>(client =>
{
    client.BaseAddress = new Uri("https://api.groq.com/");
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddTransient<IAgentProvider>(services => services.GetRequiredService<GeminiAgentProvider>());
builder.Services.AddTransient<IAgentProvider>(services => services.GetRequiredService<GroqAgentProvider>());
builder.Services.AddSingleton<AgentIdentityRegistry>();
builder.Services.AddSingleton<AgentRateLimiter>();
builder.Services.AddHostedService<ExpiredMessageWorker>();
builder.Services.AddHostedService<SiteUserSyncWorker>();
builder.Services.AddHostedService<AgentWorker>();
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("group-invites", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Items["UserId"]?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("profile-photo", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Items["UserId"]?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Profile identity is an explicit owner-only write, bounded separately from
    // photo uploads so an image operation cannot exhaust the name editor budget.
    options.AddPolicy("profile-name", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Items["UserId"]?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("encrypted-file", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Items["UserId"]?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("statuses", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Items["UserId"]?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("message-pins", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Items["UserId"]?.ToString() ?? "anonymous",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});
var app = builder.Build();
if (app.Environment.IsProduction() && !app.Services.GetRequiredService<ChatDatabase>().IsConfigured)
    throw new InvalidOperationException("Üretimde site MySQL bağlantısı zorunludur.");

app.UseMiddleware<ChatAuthenticationMiddleware>();

app.UseRateLimiter();

app.MapGroupInviteLanding();
app.MapGroupInviteEndpoints();
app.MapGroupMemberEndpoints();
app.MapStatusEndpoints();
app.MapPinEndpoints();

app.MapGet("/api/health", (ChatDatabase database) => Results.Ok(new
{
    Status = "ok",
    DatabaseConfigured = database.IsConfigured,
    ServerTime = DateTimeOffset.UtcNow
}));

if (app.Environment.IsDevelopment())
{
    app.MapPost("/api/dev/agents/test", async (IEnumerable<IAgentProvider> providers, CancellationToken cancellationToken) =>
    {
        var results = new List<object>();
        foreach (var provider in providers)
        {
            try
            {
                var answer = await provider.CompleteAsync("Yanıt olarak yalnızca OK yaz.", cancellationToken);
                results.Add(new { provider = provider.Name, available = true, responseCharacters = answer.Length });
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                results.Add(new { provider = provider.Name, available = false, error = exception.Message });
            }
        }
        return Results.Ok(results);
    });
}

app.MapPost("/api/auth/login", async (
    LoginRequest request,
    ChatState state,
    SiteAuthenticationService siteAuthentication,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        return Results.BadRequest(new ApiError("invalid_credentials", "E-posta ve parola gerekli."));
    ChatUser? user = null;
    try
    {
        var identity = await siteAuthentication.AuthenticateAsync(request, cancellationToken);
        if (identity is not null)
            user = state.UpsertSiteUser(identity.Id, identity.DisplayName, identity.Email, identity.Role);
    }
    catch (HttpRequestException)
    {
        return Results.Json(new ApiError("site_auth_unavailable", "mtkaya.me kimlik doğrulama servisine ulaşılamıyor."), statusCode: 503);
    }

    if (user is null)
        return Results.Json(new ApiError("invalid_credentials", "E-posta veya parola hatalı."), statusCode: 401);
    if (state.IsBanned(user.Id))
        return Results.Json(new ApiError("banned", "Bu hesap MTK Chat yöneticisi tarafından banlandı."), statusCode: 403);
    return Results.Ok(new LoginResponse(state.CreateSession(user.Id), user));
});

app.MapGet("/api/users", (HttpContext context, ChatState state) => Results.Ok(state.GetUsers(CurrentUserId(context))));

app.MapGet("/api/blocks", (HttpContext context, ChatState state) => Results.Ok(state.GetBlocked(CurrentUserId(context))));

app.MapPut("/api/blocks/{targetId:guid}", (Guid targetId, HttpContext context, ChatState state) =>
    state.SetBlocked(CurrentUserId(context), targetId, true)
        ? Results.NoContent()
        : Results.BadRequest(new ApiError("invalid_block", "Kullanıcı engellenemedi.")));

app.MapDelete("/api/blocks/{targetId:guid}", (Guid targetId, HttpContext context, ChatState state) =>
    state.SetBlocked(CurrentUserId(context), targetId, false)
        ? Results.NoContent()
        : Results.BadRequest(new ApiError("invalid_block", "Kullanıcı engeli kaldırılamadı.")));

app.MapPost("/api/devices", (RegisterDeviceRequest request, HttpContext context, ChatState state) =>
{
    if (string.IsNullOrWhiteSpace(request.EncryptionPublicKey) || string.IsNullOrWhiteSpace(request.SigningPublicKey))
        return Results.BadRequest(new ApiError("invalid_key", "İki cihaz anahtarı da gerekli."));
    try
    {
        using var encryption = ECDiffieHellman.Create();
        encryption.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.EncryptionPublicKey), out _);
        using var signing = ECDsa.Create();
        signing.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.SigningPublicKey), out _);
        if (encryption.KeySize != 256 || signing.KeySize != 256)
            return Results.BadRequest(new ApiError("invalid_key", "P-256 cihaz anahtarları gerekli."));
    }
    catch (Exception exception) when (exception is FormatException or CryptographicException)
    {
        return Results.BadRequest(new ApiError("invalid_key", "Cihaz anahtarı geçerli Base64 değil."));
    }
    return state.RegisterDevice(CurrentUserId(context), request)
        ? Results.NoContent()
        : Results.Conflict(new ApiError("device_key_conflict", "Bu hesap başka bir cihaz anahtarıyla kayıtlı. Mevcut prototip aynı hesapta tek cihazı destekliyor."));
});

app.MapGet("/api/users/{userId:guid}/device", (Guid userId, ChatState state) =>
    state.GetDevice(userId) is { } bundle ? Results.Ok(bundle) : Results.NotFound(new ApiError("device_not_found", "Kullanıcının kayıtlı cihazı yok.")));

app.MapConversationEndpoints();

app.MapPost("/api/messages", (SendMessageRequest request, HttpContext context, ChatState state) =>
{
    var senderId = CurrentUserId(context);
    if (!state.IsMember(senderId, request.ConversationId))
        return Results.Json(new ApiError("not_a_member", "Bu sohbetin üyesi değilsiniz."), statusCode: 403);
    if (state.IsMuted(senderId, out var mutedUntil))
        return Results.Json(new ApiError("muted", mutedUntil is null
            ? "Yönetici tarafından süresiz susturuldunuz."
            : $"{mutedUntil.Value.ToLocalTime():g} tarihine kadar susturuldunuz."), statusCode: 403);
    if (state.IsChatWriteRestricted(request.ConversationId, senderId, out var chatRestriction))
        return Results.Json(new ApiError("chat_restricted", chatRestriction), statusCode: 403);
    if (request.CreatedAt > DateTimeOffset.UtcNow.AddMinutes(5) ||
        request.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-5) ||
        request.ExpiresAt <= request.CreatedAt)
        return Results.BadRequest(new ApiError("invalid_timestamp", "Mesaj zaman bilgisi geçersiz."));
    if (request.Payloads is null || request.Payloads.Count == 0 || request.Kind is null ||
        request.Kind is not "text" and not "file" && request.Kind is not "audio/wav" && !request.Kind.StartsWith("image/", StringComparison.Ordinal))
        return Results.BadRequest(new ApiError("invalid_message", "Mesaj türü veya alıcılar geçersiz."));
    var senderDevice = state.GetDevice(senderId);
    if (senderDevice is null)
        return Results.BadRequest(new ApiError("device_not_found", "Önce cihaz anahtarını kaydedin."));
    try
    {
        foreach (var payload in request.Payloads)
        {
            if (payload is null)
                return Results.BadRequest(new ApiError("invalid_message", "Şifreli zarf boş olamaz."));
            MessageCryptography.VerifyEnvelope(payload, request.ClientMessageId, request.ConversationId,
                senderId, request.CreatedAt, senderDevice.SigningPublicKey);
        }
    }
    catch (Exception exception) when (exception is FormatException or CryptographicException or ArgumentException)
    {
        return Results.BadRequest(new ApiError("invalid_signature", "Mesaj imzası veya şifreli zarf geçersiz."));
    }
    return state.AddMessage(senderId, request, out var stored)
        ? Results.Ok(stored)
        : Results.BadRequest(new ApiError("invalid_message", "Mesaj veya alıcılar geçersiz."));
});

app.MapGet("/api/admin/users", (HttpContext context, ChatState state) =>
    RequireAdmin(context, state, () => Results.Ok(state.GetAdminUsers())));

app.MapGet("/api/admin/conversations/{conversationId:guid}/moderation", (Guid conversationId, HttpContext context, ChatState state) =>
    RequireAdmin(context, state, () => state.IsMember(CurrentUserId(context), conversationId)
        ? Results.Ok(state.GetChatModerationList(conversationId))
        : Results.Forbid()));

app.MapPut("/api/admin/conversations/{conversationId:guid}/users/{targetId:guid}/moderation",
    (Guid conversationId, Guid targetId, ChatModerationRequest request, HttpContext context, ChatState state) =>
{
    var currentId = CurrentUserId(context);
    if (!state.IsAdmin(currentId)) return Results.Json(new ApiError("admin_required", "Admin yetkisi gerekli."), statusCode: 403);
    if (!state.IsMember(currentId, conversationId)) return Results.Forbid();
    if (targetId == currentId) return Results.BadRequest(new ApiError("self_moderation", "Kendinize sohbet kısıtlaması uygulayamazsınız."));
    if (string.IsNullOrWhiteSpace(request.Action))
        return Results.BadRequest(new ApiError("invalid_moderation", "Moderasyon işlemi gerekli."));
    return state.ModerateChatUser(conversationId, targetId, request.Action, request.DurationMinutes)
        ? Results.NoContent()
        : Results.BadRequest(new ApiError("invalid_moderation", "Kullanıcı bu sohbette bulunmuyor veya işlem geçersiz."));
});

app.MapPut("/api/admin/users/{targetId:guid}/role", (Guid targetId, RoleChangeRequest request, HttpContext context, ChatState state) =>
{
    var currentId = CurrentUserId(context);
    if (!state.IsAdmin(currentId)) return Results.Json(new ApiError("admin_required", "Admin yetkisi gerekli."), statusCode: 403);
    var role = request.Role?.Trim().ToLowerInvariant();
    if (role is not ("admin" or "user")) return Results.BadRequest(new ApiError("invalid_role", "Rol admin veya user olmalıdır."));
    if (targetId == currentId && role != "admin") return Results.BadRequest(new ApiError("self_demote", "Kendi admin yetkinizi kaldıramazsınız."));
    return state.SetRole(targetId, role) ? Results.NoContent() : Results.NotFound(new ApiError("user_not_found", "Kullanıcı bulunamadı."));
});

app.MapPut("/api/admin/users/{targetId:guid}/moderation", (Guid targetId, ModerationRequest request, HttpContext context, ChatState state) =>
{
    var currentId = CurrentUserId(context);
    if (!state.IsAdmin(currentId)) return Results.Json(new ApiError("admin_required", "Admin yetkisi gerekli."), statusCode: 403);
    if (string.IsNullOrWhiteSpace(request.Action))
        return Results.BadRequest(new ApiError("invalid_moderation", "Moderasyon işlemi gerekli."));
    if (targetId == currentId && request.Action.Equals("ban", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new ApiError("self_ban", "Kendi hesabınızı banlayamazsınız."));
    return state.ModerateUser(targetId, request.Action, request.DurationMinutes)
        ? Results.NoContent()
        : Results.BadRequest(new ApiError("invalid_moderation", "Moderasyon işlemi uygulanamadı."));
});

app.MapProfileEndpoints();
app.MapEncryptedFileEndpoints();
app.MapGroupPhotoEndpoints();
app.MapCallEndpoints();
app.MapGet("/api/admin/access", () => Results.NoContent());
app.Run();

static Guid CurrentUserId(HttpContext context) => (Guid)context.Items["UserId"]!;

static IResult RequireAdmin(HttpContext context, ChatState state, Func<IResult> action) =>
    state.IsAdmin(CurrentUserId(context))
        ? action()
        : Results.Json(new ApiError("admin_required", "Admin yetkisi gerekli."), statusCode: 403);

public partial class Program;
