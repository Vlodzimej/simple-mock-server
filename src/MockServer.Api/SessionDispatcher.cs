using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using MockServer.Core;
using MockServer.Infrastructure;

namespace MockServer.Api;

public sealed class SessionDispatcher(MockDbContext database, SessionLocks locks, SessionProxy proxy, MockInstance instance, ILogger<SessionDispatcher> logger)
{
    public async Task Dispatch(HttpContext context)
    {
        var token = context.RequestAborted;
        if (context.Request.Headers[MockInstance.Header].ToString().Split(',', StringSplitOptions.TrimEntries).Contains(instance.Id))
        {
            await Failure(context, "Обнаружена петля прокси", 502);
            return;
        }
        var id = (string)context.Request.RouteValues["sessionId"]!;
        using var lease = await locks.Enter(id, token);
        var row = await database.Sessions.SingleOrDefaultAsync(item => item.Id == id, token);
        if (row is null) { await Failure(context, "Сессия не найдена", 404); return; }
        var session = JsonSerializer.Deserialize<MockSession>(row.Payload, SessionJson.Options)!;
        var path = context.Request.Path.Value![("/s/" + id).Length..];
        if (path.Length == 0) path = "/";
        var entry = new SessionRequest { SessionId = id, Method = context.Request.Method, Path = path, Source = "blocked" };
        var watch = Stopwatch.StartNew();
        try
        {
            var candidates = session.Rules.Where(rule => rule.Enabled &&
                (rule.WhenState is null || rule.WhenState == session.State) &&
                (rule.Match.Method == "*" || rule.Match.Method.Equals(context.Request.Method, StringComparison.OrdinalIgnoreCase)) &&
                RuleMatcher.PathMatches(rule.Match.Path, path) &&
                rule.Match.Query.All(pair => context.Request.Query.TryGetValue(pair.Key, out var value) && value.Count == 1 && value[0] == pair.Value) &&
                rule.Match.Headers.All(pair => context.Request.Headers.TryGetValue(pair.Key, out var value) && value.ToString() == pair.Value)).ToList();

            JsonNode? json = null;
            IFormCollection? form = null;
            if (candidates.Any(rule => rule.Match.Json.ValueKind != JsonValueKind.Undefined || rule.Match.Form.Count > 0))
            {
                context.Request.EnableBuffering(65536, 1048576);
                try
                {
                    if (context.Request.HasFormContentType) form = await context.Request.ReadFormAsync(token);
                    else if (context.Request.HasJsonContentType()) json = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: token);
                }
                finally { context.Request.Body.Position = 0; }
            }
            candidates = candidates.Where(rule =>
                (rule.Match.Json.ValueKind == JsonValueKind.Undefined || context.Request.HasJsonContentType() && RuleMatcher.JsonMatches(JsonNode.Parse(rule.Match.Json.GetRawText()), json)) &&
                rule.Match.Form.All(pair => form is not null && form.TryGetValue(pair.Key, out var value) && value.Count == 1 && value[0] == pair.Value))
                .OrderBy(rule => rule.Priority).ToList();
            if (candidates.Count > 1 && candidates[0].Priority == candidates[1].Priority)
                throw new SessionException("Неоднозначное совпадение правил: " + string.Join(", ", candidates.Where(rule => rule.Priority == candidates[0].Priority).Select(rule => rule.Id)), 409);
            if (candidates.FirstOrDefault() is not { } selected)
            {
                if (session.ProtectedPaths.Any(pattern => RuleMatcher.PathMatches(pattern, path)))
                    throw new SessionException("Защищённый путь: подходящий мок не найден", 409);
                entry.Source = "proxy";
                entry.Reason = "Правило не найдено; отправка на upstream";
                await proxy.Send(context, session, path, token);
            }
            else
            {
                entry.Source = "mock";
                entry.RuleId = selected.Id;
                entry.Reason = $"Совпало правило {selected.Id}; состояние {session.State}";
                if (selected.NextState is not null)
                {
                    session.State = selected.NextState;
                    row.Payload = JsonSerializer.Serialize(session, SessionJson.Options);
                    await database.SaveChangesAsync(token);
                }
                await Task.Delay(selected.Response.DelayMs, token);
                context.Response.StatusCode = selected.Response.Status;
                foreach (var (key, value) in selected.Response.Headers) context.Response.Headers[key] = value;
                if (!HttpMethods.IsHead(context.Request.Method))
                {
                    if (selected.Response.Json.ValueKind != JsonValueKind.Undefined)
                    {
                        context.Response.ContentType ??= "application/json; charset=utf-8";
                        await context.Response.WriteAsync(selected.Response.Json.GetRawText(), token);
                    }
                    else if (selected.Response.Text is not null)
                    {
                        context.Response.ContentType ??= "text/plain; charset=utf-8";
                        await context.Response.WriteAsync(selected.Response.Text, token);
                    }
                }
            }
        }
        catch (SessionException exception) { entry.Reason = exception.Message; await Failure(context, exception.Message, exception.Status); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or BadHttpRequestException or IOException)
        {
            entry.Reason = "Некорректное или слишком большое тело запроса";
            await Failure(context, entry.Reason, exception is BadHttpRequestException bad ? bad.StatusCode : 400);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { entry.Reason = "Клиент отменил запрос"; }
        finally
        {
            entry.Reason = context.Items["SessionProxyError"] as string ?? entry.Reason;
            entry.StatusCode = context.Response.StatusCode;
            entry.DurationMs = watch.ElapsedMilliseconds;
            try
            {
                database.SessionRequests.Add(entry);
                await database.SaveChangesAsync(CancellationToken.None);
                var cutoff = await database.SessionRequests.Where(item => item.SessionId == id).OrderByDescending(item => item.Id)
                    .Skip(4999).Select(item => (long?)item.Id).FirstOrDefaultAsync(CancellationToken.None);
                if (cutoff is not null) await database.SessionRequests.Where(item => item.SessionId == id && item.Id < cutoff).ExecuteDeleteAsync(CancellationToken.None);
            }
            catch (DbUpdateException) { logger.LogWarning("Не удалось сохранить журнал сессии {SessionId}", id); }
        }
    }

    private static async Task Failure(HttpContext context, string message, int status)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { message }, context.RequestAborted);
    }
}
