using MockServer.Core;
using MockServer.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace MockServer.Api;

public static class SessionAdmin
{
    public sealed record StateInput(string State);

    public static void MapSessionAdmin(this WebApplication app)
    {
        var routes = app.MapGroup("/admin/api/sessions");
        routes.AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (SessionException exception) { return Results.Json(new { message = exception.Message }, statusCode: exception.Status); }
        });
        routes.MapGet("", (SessionStore store, CancellationToken token) => store.List(token));
        routes.MapPost("", async (MockSession session, SessionStore store, CancellationToken token) =>
        {
            var created = await store.Create(session, token);
            return Results.Created($"/admin/api/sessions/{created.Id}", created);
        });
        routes.MapPost("/import", async (MockSession session, SessionStore store, CancellationToken token) =>
        {
            var created = await store.Create(session, token);
            return Results.Created($"/admin/api/sessions/{created.Id}", created);
        });
        routes.MapGet("/{id}", (string id, SessionStore store, CancellationToken token) => store.Get(id, token));
        routes.MapPut("/{id}", (string id, MockSession session, SessionStore store, CancellationToken token) => store.Update(id, session, token));
        routes.MapDelete("/{id}", async (string id, SessionStore store, CancellationToken token) =>
        {
            await store.Delete(id, token);
            return Results.NoContent();
        });
        routes.MapPut("/{id}/rules/{ruleId}", (string id, string ruleId, MockRule rule, SessionStore store, CancellationToken token) => store.UpsertRule(id, ruleId, rule, token));
        routes.MapDelete("/{id}/rules/{ruleId}", async (string id, string ruleId, SessionStore store, CancellationToken token) =>
        {
            await store.DeleteRule(id, ruleId, token);
            return Results.NoContent();
        });
        routes.MapPut("/{id}/state", (string id, StateInput input, SessionStore store, CancellationToken token) => store.SetState(id, input.State, token));
        routes.MapPost("/{id}/reset", (string id, SessionStore store, CancellationToken token) => store.Reset(id, token));
        routes.MapGet("/{id}/export", (string id, SessionStore store, CancellationToken token) => store.Export(id, token));
        routes.MapGet("/{id}/requests", (string id, int? limit, string? ruleId, string? method, int? statusCode,
            string? source, string? search, DateTime? from, DateTime? to, long? beforeId, SessionStore store, CancellationToken token) =>
            store.Requests(id, token, limit ?? 100, ruleId, method, statusCode, source, search, from, to, beforeId));
        routes.MapGet("/{id}/requests/{requestId:long}", (string id, long requestId, SessionStore store, CancellationToken token) => store.Request(id, requestId, token));
        routes.MapDelete("/{id}/requests", async (string id, [FromBody] JournalDeletion deletion, SessionStore store, CancellationToken token) =>
            Results.Ok(new { deleted = await store.DeleteRequests(id, deletion, token) }));
        routes.MapPost("/{id}/verify", (string id, RequestVerification input, SessionStore store, CancellationToken token) => store.Verify(id, input, token));
    }
}
