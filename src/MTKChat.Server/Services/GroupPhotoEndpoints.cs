using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class GroupPhotoEndpoints
{
    private static readonly SemaphoreSlim UploadSlots = new(2);

    public static void MapGroupPhotoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/conversations/{conversationId:guid}/photo", (Guid conversationId, HttpContext context, ChatState state) =>
        {
            var actor = (Guid)context.Items["UserId"]!;
            if (!state.IsMember(actor, conversationId)) return Results.StatusCode(403);
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return state.GetGroupPhoto(actor, conversationId) is { } photo
                ? Results.File(photo.Jpeg, photo.ContentType) : Results.NotFound();
        });

        app.MapPut("/api/conversations/{conversationId:guid}/photo", async (Guid conversationId, HttpContext context, ChatState state) =>
        {
            var actor = (Guid)context.Items["UserId"]!;
            if (!state.IsMember(actor, conversationId) || state.GetUser(actor)?.IsAgent != false) return Results.StatusCode(403);
            if (!await UploadSlots.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
            try
            {
                if (context.Request.ContentLength > ProfilePhotoCodec.MaxUploadBytes) return Results.StatusCode(413);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int count;
                while ((count = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    if (buffer.Length + count > ProfilePhotoCodec.MaxUploadBytes) return Results.StatusCode(413);
                    buffer.Write(chunk, 0, count);
                }
                try
                {
                    var photo = ProfilePhotoCodec.Normalize(buffer.ToArray());
                    return state.SetGroupPhoto(actor, conversationId, photo) is { } result
                        ? Results.Ok(result) : Results.StatusCode(403);
                }
                catch (InvalidDataException exception)
                {
                    return Results.BadRequest(new ApiError("invalid_photo", exception.Message));
                }
            }
            finally { UploadSlots.Release(); }
        }).RequireRateLimiting("profile-photo");

        app.MapDelete("/api/conversations/{conversationId:guid}/photo", (Guid conversationId, HttpContext context, ChatState state) =>
            state.SetGroupPhoto((Guid)context.Items["UserId"]!, conversationId, null) is { } result
                ? Results.Ok(result) : Results.StatusCode(403)).RequireRateLimiting("profile-photo");
    }
}
