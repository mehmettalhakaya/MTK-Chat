namespace MTKChat.Server.Services;

public static class InviteLandingPage
{
    private static readonly IReadOnlyDictionary<string, string> Assets = new Dictionary<string, string>
    {
        ["index.html"] = Read("index.html"),
        ["invite.css"] = Read("invite.css"),
        ["invite.js"] = Read("invite.js")
    };

    private static string Read(string name)
    {
        using var stream = typeof(InviteLandingPage).Assembly.GetManifestResourceStream("MTKChat.InviteWeb." + name)
            ?? throw new InvalidOperationException("Davet sayfası kaynağı bulunamadı.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void MapGroupInviteLanding(this WebApplication app)
    {
        // URL fragments never reach this service/proxy. Crawlers see a generic
        // page, not a token-bearing URL, group title or automatic join action.
        app.MapGet("/invite", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            // A relative canonical redirect preserves the proxy's /chat prefix
            // and the browser fragment, without trusting X-Forwarded-Host.
            return context.Request.Path.Value?.EndsWith('/') == true
                ? Serve(context, "index.html", "text/html; charset=utf-8")
                : Results.Redirect("invite/");
        });
        app.MapGet("/invite-assets/invite.css", (HttpContext context) => Serve(context, "invite.css", "text/css; charset=utf-8"));
        app.MapGet("/invite-assets/invite.js", (HttpContext context) => Serve(context, "invite.js", "text/javascript; charset=utf-8"));
        app.MapPost("/api/auth/logout", (HttpContext context, ChatState state) =>
        {
            // The authentication middleware has already validated this bearer.
            var header = context.Request.Headers.Authorization.ToString();
            state.RevokeSession(header[7..]);
            context.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        });
    }

    private static IResult Serve(HttpContext context, string asset, string contentType)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; base-uri 'none'; form-action 'self'; " +
            "script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'";
        return Results.Content(Assets[asset], contentType);
    }
}
