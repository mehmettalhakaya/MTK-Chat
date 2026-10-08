using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class PinEndpoints
{
    public static void MapPinEndpoints(this WebApplication app)
    {
        app.MapGet("/api/conversations/{id:guid}/pins", (Guid id, HttpContext context, ChatState state) =>
            state.GetPinnedMessages((Guid)context.Items["UserId"]!, id) is { } pins ? Results.Ok(pins) : Failure(PinMessageResult.Forbidden));
        app.MapPost("/api/conversations/{id:guid}/pins", (Guid id, PinMessageRequest request, HttpContext context, ChatState state) =>
        {
            var status = state.PinMessage((Guid)context.Items["UserId"]!, id, request, out var pin);
            return status == PinMessageResult.Success ? Results.Ok(pin) : Failure(status);
        }).RequireRateLimiting("message-pins");
        app.MapDelete("/api/conversations/{id:guid}/pins/{messageId:guid}",
            (Guid id, Guid messageId, HttpContext context, ChatState state) =>
        {
            var status = state.UnpinMessage((Guid)context.Items["UserId"]!, id, messageId);
            return status == PinMessageResult.Success ? Results.NoContent() : Failure(status);
        }).RequireRateLimiting("message-pins");
    }

    private static IResult Failure(PinMessageResult status) => status switch
    {
        PinMessageResult.InvalidDuration => Results.BadRequest(new ApiError("invalid_pin", "Geçerli bir mesaj ve 24 saat, 7 gün veya 30 gün seçin.")),
        PinMessageResult.NotFound => Results.NotFound(new ApiError("pin_message_unavailable", "Mesaj veya sabitleme artık erişilebilir değil.")),
        PinMessageResult.CapacityReached => Results.Conflict(new ApiError("pin_limit", "Bir sohbette en fazla 3 mesaj sabitlenebilir. Önce bir sabitlemeyi kaldırın.")),
        _ => Results.Json(new ApiError("pin_forbidden", "Bu işlem için sohbet üyeliği ve uygun yetki gerekli."), statusCode: 403)
    };
}
