using Microsoft.AspNetCore.Http.Features;
using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class StatusEndpoints
{
    private const long MaximumRequestBytes = 1_400_000;
    public static void MapStatusEndpoints(this WebApplication app)
    {
        // A middleware limit runs BEFORE JSON binding, unlike endpoint filters.
        // Set the server feature for chunked uploads as well as Content-Length.
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) &&
                string.Equals(context.Request.Path.Value, "/api/statuses", StringComparison.OrdinalIgnoreCase))
            {
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false }) feature.MaxRequestBodySize = MaximumRequestBytes;
                if (context.Request.ContentLength > MaximumRequestBytes)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                    await context.Response.WriteAsJsonAsync(new ApiError("status_too_large", "Durum isteği boyut sınırını aşıyor."));
                    return;
                }
            }
            await next(context);
        });
        app.MapGet("/api/statuses", (HttpContext context, ChatState state) =>
            Results.Ok(state.GetStatuses(User(context))));
        app.MapGet("/api/statuses/{id:guid}", (Guid id, HttpContext context, ChatState state) =>
            state.GetStatus(User(context), id) is { } status ? Results.Ok(status) : Failure(StatusResult.NotFound));
        app.MapPost("/api/statuses", (SendStatusRequest? request, HttpContext context, ChatState state) =>
        {
            var result = state.AddStatus(User(context), request, out var status);
            return result == StatusResult.Success ? Results.Ok(status) : Failure(result);
        }).RequireRateLimiting("statuses").WithMetadata(new StatusBodyLimit());
        app.MapDelete("/api/statuses/{id:guid}", (Guid id, HttpContext context, ChatState state) =>
        {
            var result = state.DeleteStatus(User(context), id);
            return result == StatusResult.Success ? Results.NoContent() : Failure(result);
        }).RequireRateLimiting("statuses");
    }

    // Kestrel/HTTP binding checks this before deserializing the large base64 body.
    // A status contains <=512KiB body plus <=51 small signed key envelopes.
    private sealed class StatusBodyLimit : Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata
    {
        public long? MaxRequestBodySize => MaximumRequestBytes;
    }

    private static Guid User(HttpContext context) => (Guid)context.Items["UserId"]!;
    private static IResult Failure(StatusResult result) => result switch
    {
        StatusResult.Invalid => Results.BadRequest(new ApiError("invalid_status", "Durumun türü, şifreli içeriği veya seçilen alıcıları geçersiz.")),
        StatusResult.NotFound => Results.NotFound(new ApiError("status_unavailable", "Durum bulunamadı, süresi doldu veya sizinle paylaşılmadı.")),
        StatusResult.CapacityReached => Results.Conflict(new ApiError("status_limit", "Durum sınırına ulaşıldı. Eski durumları silin veya sürelerinin dolmasını bekleyin.")),
        StatusResult.ReplayConflict => Results.Conflict(new ApiError("status_replay_conflict", "Bu durum kimliği farklı bir içerikle daha önce kullanılmış.")),
        _ => Results.Json(new ApiError("status_denied", "Bu durum işlemi için yetkiniz yok veya seçilen bir kullanıcıyla iletişim kısıtlı."), statusCode: 403)
    };
}
