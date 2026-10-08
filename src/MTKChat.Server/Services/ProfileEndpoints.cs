using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class ProfileEndpoints
{
    private static readonly SemaphoreSlim UploadSlots = new(2);
    public static void MapProfileEndpoints(this WebApplication app)
    {
        app.MapGet("/api/profile", (HttpContext context, ChatState state) =>
            Results.Ok(state.GetUser((Guid)context.Items["UserId"]!)));
        app.MapPut("/api/profile/name", (ProfileNameChangeRequest request, HttpContext context, ChatState state) =>
        {
            // An extra JSON userId/role/email is deliberately irrelevant: the
            // authenticated owner is the only identity this endpoint can edit.
            context.Response.Headers.CacheControl = "no-store";
            var result = state.SetProfileName((Guid)context.Items["UserId"]!, request.FirstName, request.LastName, out var user);
            return result switch
            {
                ProfileNameChangeStatus.Success => Results.Ok(user),
                ProfileNameChangeStatus.InvalidName => Results.BadRequest(new ApiError("invalid_profile_name",
                    "İsim ve soyisim gerekli. Her alan en fazla 80 karakter olabilir; harf, boşluk, tire ve kesme işareti kullanın.")),
                _ => Results.Json(new ApiError("profile_name_denied", "Bu hesabın profili düzenlenemez."), statusCode: 403)
            };
        }).RequireRateLimiting("profile-name");
        app.MapGet("/api/profile/privacy", (HttpContext context, ChatState state) =>
            Results.Ok(state.GetPrivacy((Guid)context.Items["UserId"]!)));
        app.MapPut("/api/profile/privacy", (PrivacySettings request, HttpContext context, ChatState state) =>
            state.SetPrivacy((Guid)context.Items["UserId"]!, request) ? Results.Ok(request) : Results.StatusCode(403));

        app.MapPut("/api/profile/photo", async (HttpContext context, ChatState state) =>
        {
            // Bound both decoding memory and concurrent request buffering before accepting the body.
            if (!await UploadSlots.WaitAsync(0, context.RequestAborted))
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            try
            {
                if (context.Request.ContentLength > ProfilePhotoCodec.MaxUploadBytes)
                    return Results.Json(new ApiError("photo_too_large", "Fotoğraf en fazla 2 MB olabilir."), statusCode: 413);
                using var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
                {
                    if (buffer.Length + read > ProfilePhotoCodec.MaxUploadBytes)
                        return Results.Json(new ApiError("photo_too_large", "Fotoğraf en fazla 2 MB olabilir."), statusCode: 413);
                    buffer.Write(chunk, 0, read);
                }
                try
                {
                    var photo = ProfilePhotoCodec.Normalize(buffer.ToArray());
                    // The owner always comes from the authenticated session, never a client-supplied ID.
                    var user = state.SetProfilePhoto((Guid)context.Items["UserId"]!, photo);
                    return user is null ? Results.NotFound() : Results.Ok(user);
                }
                catch (InvalidDataException exception)
                {
                    return Results.BadRequest(new ApiError("invalid_photo", exception.Message));
                }
            }
            finally { UploadSlots.Release(); }
        }).RequireRateLimiting("profile-photo");

        app.MapDelete("/api/profile/photo", (HttpContext context, ChatState state) =>
        {
            var user = state.SetProfilePhoto((Guid)context.Items["UserId"]!, null);
            return user is null ? Results.NotFound() : Results.Ok(user);
        }).RequireRateLimiting("profile-photo");

        app.MapGet("/api/users/{userId:guid}/photo", (Guid userId, HttpContext context, ChatState state) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return state.GetProfilePhoto(userId) is { } photo
                ? Results.File(photo.Jpeg, photo.ContentType) : Results.NotFound();
        });
    }
}
