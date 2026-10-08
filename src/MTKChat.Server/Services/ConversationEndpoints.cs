using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this WebApplication app)
    {
        static Guid User(HttpContext c) => (Guid)c.Items["UserId"]!;
        static IResult Forbidden() => Results.Json(new ApiError("not_a_member", "Bu sohbetin üyesi değilsiniz."), statusCode: 403);
        app.MapGet("/api/conversations", (HttpContext c, ChatState state) => Results.Ok(state.GetConversations(User(c))));
        app.MapGet("/api/groups/manageable", (HttpContext c, ChatState state) => Results.Ok(state.GetManageableGroups(User(c))));
        app.MapPut("/api/conversations/{id:guid}/title",
            (Guid id, RenameConversationRequest request, HttpContext c, ChatState state) =>
            {
                var status = state.RenameGroup(User(c), id, request.Title, out var result);
                return status switch
                {
                    RenameGroupResult.Renamed => Results.Ok(result),
                    RenameGroupResult.InvalidTitle => Results.BadRequest(new ApiError("invalid_group_title", "Grup adı 1–80 karakter olmalı ve tek satırdan oluşmalıdır.")),
                    RenameGroupResult.NotAGroup => Results.BadRequest(new ApiError("not_a_group", "Özel sohbetin adı değiştirilemez.")),
                    RenameGroupResult.NotFound => Results.NotFound(new ApiError("conversation_not_found", "Grup bulunamadı.")),
                    _ => Results.Json(new ApiError("group_rename_denied", "Grup adını yalnızca grup yöneticisi veya site admini değiştirebilir."), statusCode: 403)
                };
            });
        app.MapPost("/api/conversations", (CreateConversationRequest request, HttpContext c, ChatState state) =>
            state.CreateConversation(User(c), request) is { } group ? Results.Ok(group) :
                Results.BadRequest(new ApiError("invalid_conversation", "1–80 karakterlik grup adı ve 1–49 uygun üye seçin; engellenen/yasaklı hesaplar eklenemez.")));
        app.MapPost("/api/conversations/direct", (CreateDirectConversationRequest request, HttpContext c, ChatState state) =>
            state.GetOrCreateDirect(User(c), request.UserId) is { } direct ? Results.Ok(direct) :
                Results.Json(new ApiError("direct_unavailable", "Bu kullanıcıyla sohbet açılamadı. Hesap veya engel durumunu kontrol edin."), statusCode: 403));
        app.MapDelete("/api/conversations/{id:guid}", (Guid id, HttpContext c, ChatState state) =>
            state.RemoveConversationForMe(User(c), id) ? Results.NoContent() : Forbidden());
        app.MapPost("/api/conversations/{id:guid}/reopen", (Guid id, HttpContext c, ChatState state) =>
            state.ReopenConversation(User(c), id) is { } room ? Results.Ok(room) : Forbidden());
        app.MapPost("/api/conversations/{id:guid}/leave", (Guid id, HttpContext c, ChatState state) =>
            state.LeaveGroup(User(c), id) switch
            {
                LeaveGroupResult.Left => Results.NoContent(),
                LeaveGroupResult.AdminTransferRequired => Results.Conflict(new ApiError("group_admin_transfer_required",
                    "Gruptan çıkmadan önce başka bir üyeyi grup yöneticisi yapın.")),
                LeaveGroupResult.NotAGroup => Results.BadRequest(new ApiError("not_a_group", "Özel sohbetten çıkılmaz; sohbeti yalnızca kendiniz için silebilirsiniz.")),
                LeaveGroupResult.NotFound => Results.NotFound(new ApiError("conversation_not_found", "Sohbet bulunamadı.")),
                _ => Forbidden()
            });
        app.MapDelete("/api/messages/{messageId:guid}", (Guid messageId, bool? forEveryone, HttpContext c, ChatState state) =>
            state.DeleteMessage(User(c), messageId, forEveryone == true) switch
            {
                DeleteResult.Deleted => Results.NoContent(),
                DeleteResult.Forbidden => Forbidden(),
                DeleteResult.WindowExpired => Results.Conflict(new ApiError("delete_window_expired",
                    "Herkesten silme yalnızca mesajın sunucuya ulaşmasından sonraki ilk 15 dakikada kullanılabilir.")),
                _ => Results.NotFound(new ApiError("message_not_found", "Mesaj bulunamadı."))
            });
        app.MapDelete("/api/conversations/{id:guid}/messages", (Guid id, bool? forEveryone, HttpContext c, ChatState state) =>
        {
            if (forEveryone == true) return Results.Json(new ApiError("personal_clear_only",
                "Sohbet mesajları topluca yalnızca kendi hesabınızdan temizlenebilir."), statusCode: 403);
            var count = state.ClearHistory(User(c), id, false);
            return count < 0 ? Forbidden() : Results.Ok(new { deleted = count });
        });
        app.MapGet("/api/conversations/{id:guid}/members", (Guid id, HttpContext c, ChatState state) =>
            state.IsMember(User(c), id) ? Results.Ok(state.GetMembers(id).Select(state.GetUser)) : Forbidden());
        app.MapGet("/api/conversations/{id:guid}/presence", (Guid id, HttpContext c, ChatState state) =>
            state.IsMember(User(c), id) ? Results.Ok(state.GetPresence(id, User(c))) : Forbidden());
        app.MapPut("/api/conversations/{id:guid}/members/{target:guid}/role",
            (Guid id, Guid target, GroupRoleChangeRequest request, HttpContext c, ChatState state) =>
            state.SetGroupRole(User(c), id, target, request.Role) ? Results.NoContent() :
                Results.Json(new ApiError("group_role_denied", "Grup rolü değiştirilemedi."), statusCode: 403));
        app.MapPut("/api/conversations/{id:guid}/members/{target:guid}/moderation",
            (Guid id, Guid target, ChatModerationRequest request, HttpContext c, ChatState state) =>
            state.ModerateGroupUser(User(c), id, target, request.Action, request.DurationMinutes)
                ? Results.NoContent() : Results.Json(new ApiError("group_moderation_denied", "Grup moderasyonu yapılamadı."), statusCode: 403));
        app.MapPost("/api/conversations/{id:guid}/presence", (Guid id, HttpContext c, ChatState state) =>
        {
            if (!state.IsMember(User(c), id)) return Forbidden();
            state.TouchActivity(User(c), id);
            return Results.NoContent();
        });
        app.MapGet("/api/conversations/{id:guid}/messages", (Guid id, DateTimeOffset? after, HttpContext c, ChatState state) =>
            state.IsMember(User(c), id) ? Results.Ok(state.GetMessages(User(c), id, after)) : Forbidden());
        app.MapGet("/api/messages/inbox", (HttpContext c, ChatState state) => Results.Ok(state.GetDeliveryInbox(User(c))));
        app.MapPost("/api/messages/ack", (AcknowledgeMessagesRequest request, HttpContext c, ChatState state) =>
        {
            if (request.MessageIds is null || request.MessageIds.Count is < 1 or > 100 || request.MessageIds.Contains(Guid.Empty))
                return Results.BadRequest(new ApiError("invalid_receipts", "1–100 geçerli mesaj kimliği gönderin."));
            state.AcknowledgeMessages(User(c), request.MessageIds, request.Read);
            return Results.NoContent();
        });
    }
}
