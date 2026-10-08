using MTKChat.Contracts;

namespace MTKChat.Server.Services;

public static class CallEndpoints
{
    public static void MapCallEndpoints(this WebApplication app)
    {
        static Guid User(HttpContext c) => (Guid)c.Items["UserId"]!;
        static IResult Run(Func<IResult> action)
        {
            try { return action(); }
            catch (CallFailure error) { return Results.Json(new ApiError("call_unavailable", error.Message), statusCode: error.Status); }
        }
        app.MapGet("/api/calls", (HttpContext c, CallRegistry calls) => Results.Ok(calls.List(User(c))));
        app.MapPost("/api/calls", (StartCallRequest r, HttpContext c, CallRegistry calls) => Run(() => Results.Ok(calls.Start(User(c), r))));
        app.MapGet("/api/calls/{id:guid}", (Guid id, HttpContext c, CallRegistry calls) => Run(() => Results.Ok(calls.Get(id, User(c)))));
        app.MapPost("/api/calls/{id:guid}/join", (Guid id, CallJoin r, HttpContext c, CallRegistry calls) => Run(() => Results.Ok(calls.Join(id, User(c), r))));
        app.MapGet("/api/calls/{id:guid}/frames", (Guid id, HttpContext c, CallRegistry calls) => Run(() => Results.Ok(calls.Receive(id, User(c)))));
        app.MapPost("/api/calls/{id:guid}/frames", (Guid id, CallFrame[] r, HttpContext c, CallRegistry calls) =>
            Run(() => { calls.Send(id, User(c), r); return Results.NoContent(); }))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(192 * 1024));
        app.MapPut("/api/calls/{id:guid}/mute", (Guid id, CallMute r, HttpContext c, CallRegistry calls) =>
            Run(() => { calls.Mute(id, User(c), r.Muted); return Results.NoContent(); }));
        app.MapDelete("/api/calls/{id:guid}/participation", (Guid id, HttpContext c, CallRegistry calls) =>
            Run(() => { calls.Leave(id, User(c)); return Results.NoContent(); }));
    }
}
