using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class GroupInviteEndpoints
{
    public static void MapGroupInviteEndpoints(this WebApplication app)
    {
        app.MapGet("/api/conversations/{id:guid}/invites", (Guid id, HttpContext context, ChatState state) =>
        {
            var status = state.ListGroupInvites(User(context), id, out var invites);
            return status == GroupInviteStatus.Success ? Results.Ok(invites) : Failure(status);
        }).RequireRateLimiting("group-invites");

        app.MapPost("/api/conversations/{id:guid}/invites", (Guid id, CreateGroupInviteRequest? request, HttpContext context, ChatState state) =>
        {
            if (request is null) return Failure(GroupInviteStatus.InvalidDuration);
            var status = state.CreateGroupInvite(User(context), id, request.DurationMinutes, out var invite, request.NeverExpires);
            return status == GroupInviteStatus.Success ? Results.Ok(invite) : Failure(status);
        }).RequireRateLimiting("group-invites");

        app.MapPut("/api/conversations/{id:guid}/invites/{inviteId:guid}",
            (Guid id, Guid inviteId, ChangeGroupInviteDurationRequest? request, HttpContext context, ChatState state) =>
        {
            if (request is null) return Failure(GroupInviteStatus.InvalidDuration);
            var status = state.ChangeGroupInviteDuration(User(context), id, inviteId, request.DurationMinutes,
                out var invite, request.NeverExpires);
            return status == GroupInviteStatus.Success ? Results.Ok(invite) : Failure(status);
        }).RequireRateLimiting("group-invites");

        app.MapDelete("/api/conversations/{id:guid}/invites/{inviteId:guid}",
            (Guid id, Guid inviteId, HttpContext context, ChatState state) =>
        {
            var status = state.DeleteGroupInvite(User(context), id, inviteId);
            return status == GroupInviteStatus.Success ? Results.NoContent() : Failure(status);
        }).RequireRateLimiting("group-invites");

        app.MapPost("/api/conversations/{id:guid}/invite", (Guid id, HttpContext context, ChatState state) =>
        {
            var status = state.CreateOrRotateGroupInvite(User(context), id, out var invite);
            return status == GroupInviteStatus.Success ? Results.Ok(invite) : Failure(status);
        }).RequireRateLimiting("group-invites");

        app.MapDelete("/api/conversations/{id:guid}/invite", (Guid id, HttpContext context, ChatState state) =>
        {
            var status = state.RevokeGroupInvite(User(context), id);
            return status == GroupInviteStatus.Success ? Results.NoContent() : Failure(status);
        }).RequireRateLimiting("group-invites");

        // Tokens belong in the JSON body, never a path/query that reverse-proxy access
        // logs retain. Preview is authenticated and deliberately does not admit anyone.
        app.MapPost("/api/group-invites/preview", (GroupInviteTokenRequest? request, HttpContext context, ChatState state) =>
        {
            if (!ValidToken(request?.Token)) return InvalidToken();
            var status = state.PreviewGroupInvite(User(context), request!.Token, out var preview);
            return status == GroupInviteStatus.Success ? Results.Ok(preview) : Failure(status);
        }).RequireRateLimiting("group-invites");

        app.MapPost("/api/group-invites/join", (GroupInviteTokenRequest? request, HttpContext context, ChatState state) =>
        {
            if (!ValidToken(request?.Token)) return InvalidToken();
            var status = state.JoinGroupInvite(User(context), request!.Token, out var conversation);
            return status == GroupInviteStatus.Success ? Results.Ok(conversation) : Failure(status);
        }).RequireRateLimiting("group-invites");
    }

    private static Guid User(HttpContext context) => (Guid)context.Items["UserId"]!;

    private static bool ValidToken(string? token) => token is { Length: 43 } &&
        token.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    private static IResult InvalidToken() => Results.BadRequest(new ApiError("invalid_invite_token", "Davet bağlantısı geçersiz."));

    private static IResult Failure(GroupInviteStatus status) => status switch
    {
        GroupInviteStatus.NotFound => Results.NotFound(new ApiError("conversation_not_found", "Grup bulunamadı.")),
        GroupInviteStatus.NotAGroup => Results.BadRequest(new ApiError("not_a_group", "Davet bağlantısı yalnızca gruplar için oluşturulabilir.")),
        GroupInviteStatus.InvalidOrExpired => Results.Json(new ApiError("invite_unavailable", "Davet bağlantısı süresi dolmuş veya iptal edilmiş."), statusCode: 410),
        GroupInviteStatus.CapacityReached => Results.Conflict(new ApiError("group_full", "Grubun üye sınırına ulaşıldı.")),
        GroupInviteStatus.InvalidDuration => Results.BadRequest(new ApiError("invalid_invite_duration", "Davet süresi 5 dakika ile 365 gün arasında veya Sınırsız olmalıdır.")),
        GroupInviteStatus.InviteNotFound => Results.NotFound(new ApiError("invite_not_found", "Davet bağlantısı bulunamadı veya silinmiş.")),
        GroupInviteStatus.InviteLimitReached => Results.Conflict(new ApiError("invite_limit", "Bir grupta en fazla 50 davet bağlantısı saklanabilir. Kullanmadıklarınızı silin.")),
        _ => Results.Json(new ApiError("group_invite_denied", "Bu davet işlemi için yetkiniz yok veya hesap/grup erişiminiz kısıtlanmış."), statusCode: 403)
    };
}
