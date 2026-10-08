using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class GroupMemberEndpoints
{
    public static void MapGroupMemberEndpoints(this WebApplication app)
    {
        app.MapPost("/api/conversations/{id:guid}/members/{target:guid}/remove",
            (Guid id, Guid target, HttpContext context, ChatState state, CallRegistry calls) =>
            {
                context.Response.Headers.CacheControl = "no-store";
                var status = state.RemoveGroupMember((Guid)context.Items["UserId"]!, id, target);
                if (status == GroupMemberRemovalStatus.Removed)
                {
                    calls.RevokeConversationMember(id, target);
                    return Results.NoContent();
                }
                return Failure(status);
            }).RequireRateLimiting("group-invites");
    }

    private static IResult Failure(GroupMemberRemovalStatus status) => status switch
    {
        GroupMemberRemovalStatus.NotFound => Results.NotFound(new ApiError("group_member_not_found", "Grup veya üye bulunamadı.")),
        GroupMemberRemovalStatus.NotAGroup => Results.BadRequest(new ApiError("not_a_group", "Üye çıkarma yalnızca gruplarda kullanılabilir.")),
        GroupMemberRemovalStatus.ProtectedTarget => Results.Conflict(new ApiError("group_member_protected", "Kendinizi, botları veya korunan yöneticileri bu işlemle çıkaramazsınız.")),
        _ => Results.Json(new ApiError("group_member_remove_denied", "Üye çıkarmak için grup yöneticisi yetkisi gerekli."), statusCode: 403)
    };
}
