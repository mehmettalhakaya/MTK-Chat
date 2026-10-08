using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public sealed class ChatAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ChatState state, ChatDatabase database)
    {
        // Apply before auth, binding and rate limiting as those can short-circuit
        // the endpoint. Personal profile data, capability links and membership
        // previews must never cache, including errors before model binding.
        if (context.Request.Path.StartsWithSegments("/api/profile") ||
            context.Request.Path.StartsWithSegments("/api/statuses") ||
            context.Request.Path.StartsWithSegments("/api/blocks") ||
            context.Request.Path.StartsWithSegments("/api/group-invites") ||
            IsPinPath(context.Request.Path.Value?.TrimEnd('/')) ||
            IsInviteManagementPath(context.Request.Path.Value?.TrimEnd('/')) ||
            IsMemberRemovalPath(context.Request.Path.Value?.TrimEnd('/')))
            context.Response.Headers.CacheControl = "no-store";
        if (context.Request.Path.StartsWithSegments("/api") &&
            !context.Request.Path.StartsWithSegments("/api/auth/login") && !context.Request.Path.StartsWithSegments("/api/health"))
        {
            var header = context.Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : "";
            var user = state.ResolveSession(token);
            if (user is null)
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new ApiError("unauthorized", "Oturum geçersiz veya süresi dolmuş."));
                return;
            }
            var groupPrivilege = RequiresFreshGroupAuthority(context.Request.Path, context.Request.Method);
            if (context.Request.Path.StartsWithSegments("/api/admin") || groupPrivilege)
            {
                // Refresh AFTER authentication; unauthenticated traffic must not cause database syncs.
                if (database.IsConfigured) state.SynchronizeSiteUsers(database.ReadSiteAccounts());
                if (state.ResolveSession(token) is null || context.Request.Path.StartsWithSegments("/api/admin") && !state.IsAdmin(user.Value))
                {
                    context.Response.StatusCode = 403;
                    await context.Response.WriteAsJsonAsync(new ApiError("admin_required", "Site yöneticisi yetkisi gerekli."));
                    return;
                }
            }
            context.Items["UserId"] = user.Value;
            state.TouchActivity(user.Value);
        }
        await next(context);
    }

    private static bool RequiresFreshGroupAuthority(PathString path, string method)
    {
        if (path.StartsWithSegments("/api/groups/manageable")) return true;
        var value = path.Value?.TrimEnd('/');
        // ASP.NET routing is case-insensitive, so authority refresh must use the
        // same comparison; a differently cased URL cannot bypass a revoked role.
        if (value is null || !value.StartsWith("/api/conversations/", StringComparison.OrdinalIgnoreCase)) return false;
        if (IsInviteManagementPath(value))
            return HttpMethods.IsPost(method) || HttpMethods.IsDelete(method) || HttpMethods.IsPut(method) ||
                HttpMethods.IsGet(method) && value.EndsWith("/invites", StringComparison.OrdinalIgnoreCase);
        if (IsPinPath(value)) return HttpMethods.IsPost(method) || HttpMethods.IsDelete(method);
        if (HttpMethods.IsPost(method) && IsMemberRemovalPath(value)) return true;
        if (HttpMethods.IsPut(method))
            return value.EndsWith("/role", StringComparison.OrdinalIgnoreCase) || value.EndsWith("/moderation", StringComparison.OrdinalIgnoreCase) ||
                value.EndsWith("/title", StringComparison.OrdinalIgnoreCase);
        // Creating/rotating or revoking an invitation is a group-admin operation. A
        // site's revoked admin role must be refreshed before ChatState authorizes it.
        return false;
    }

    private static bool IsInviteManagementPath(string? value)
    {
        if (value is null) return false;
        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is not (4 or 5) || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) ||
            !segments[1].Equals("conversations", StringComparison.OrdinalIgnoreCase) || !Guid.TryParse(segments[2], out _)) return false;
        return segments.Length == 4 && segments[3].Equals("invite", StringComparison.OrdinalIgnoreCase) ||
            segments[3].Equals("invites", StringComparison.OrdinalIgnoreCase) &&
                (segments.Length == 4 || Guid.TryParse(segments[4], out _));
    }

    private static bool IsMemberRemovalPath(string? value)
    {
        var parts = value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 6 } && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("conversations", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[2], out _) &&
            parts[3].Equals("members", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(parts[4], out _) &&
            parts[5].Equals("remove", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPinPath(string? value)
    {
        var parts = value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 4 or 5 } && parts[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("conversations", StringComparison.OrdinalIgnoreCase) &&
            parts[3].Equals("pins", StringComparison.OrdinalIgnoreCase);
    }
}
