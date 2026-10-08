using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class EncryptedFileEndpoints
{
    private const int MaxCipherBytes = 5_242_880;
    private static readonly SemaphoreSlim UploadSlots = new(2);

    public static void MapEncryptedFileEndpoints(this WebApplication app)
    {
        app.MapPost("/api/conversations/{roomId:guid}/files/{clientMessageId:guid}",
            async (Guid roomId, Guid clientMessageId, HttpContext context, ChatState state) =>
            {
                var senderId = (Guid)context.Items["UserId"]!;
                if (!state.IsMember(senderId, roomId) || state.IsMuted(senderId, out _) || state.IsChatWriteRestricted(roomId, senderId, out _))
                    return Results.StatusCode(403);
                if (context.Request.ContentLength > MaxCipherBytes) return Results.StatusCode(413);
                if (!await UploadSlots.WaitAsync(0, context.RequestAborted)) return Results.StatusCode(429);
                try
                {
                    using var buffer = new MemoryStream();
                    var chunk = new byte[16_384];
                    int read;
                    while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                    {
                        if (buffer.Length + read > MaxCipherBytes) return Results.StatusCode(413);
                        buffer.Write(chunk, 0, read);
                    }
                    var token = state.UploadEncryptedFile(senderId, roomId, clientMessageId, buffer.ToArray());
                    return token is null ? Results.BadRequest(new ApiError("invalid_file", "Şifreli dosya kaydedilemedi.")) :
                        Results.Ok(new FileUploadResult(token));
                }
                finally { UploadSlots.Release(); }
            }).RequireRateLimiting("encrypted-file");

        app.MapGet("/api/files/{token}", (string token, HttpContext context, ChatState state) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return state.DownloadEncryptedFile((Guid)context.Items["UserId"]!, token) is { } ciphertext
                ? Results.File(ciphertext, "application/octet-stream") : Results.NotFound();
        });
    }
}
