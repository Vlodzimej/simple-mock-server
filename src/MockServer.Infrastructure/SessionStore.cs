using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MockServer.Core;

namespace MockServer.Infrastructure;

public sealed class SessionLocks : IDisposable
{
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task<IDisposable> Enter(string id, CancellationToken token)
    {
        var gate = gates[(uint)StringComparer.Ordinal.GetHashCode(id) % gates.Length];
        await gate.WaitAsync(token);
        return new Lease(gate);
    }
    private sealed class Lease(SemaphoreSlim gate) : IDisposable { public void Dispose() => gate.Release(); }
    public void Dispose() { foreach (var gate in gates) gate.Dispose(); }
}

public sealed class SessionStore(MockDbContext database, SessionLocks locks)
{
    public async Task<List<MockSession>> List(CancellationToken token) =>
        (await database.Sessions.AsNoTracking().OrderBy(row => row.Id).ToListAsync(token)).Select(Decode).ToList();

    public async Task<MockSession> Get(string id, CancellationToken token) => Decode(await Find(id, token));

    public async Task<MockSession> Create(MockSession session, CancellationToken token)
    {
        session.Id = Guid.NewGuid().ToString("N");
        session.State = session.InitialState;
        SessionValidation.Validate(session);
        database.Sessions.Add(new SessionRow { Id = session.Id, Payload = Encode(session) });
        await database.SaveChangesAsync(token);
        return session;
    }

    public async Task<MockSession> Update(string id, MockSession session, CancellationToken token) =>
        await Change(id, stored =>
        {
            session.Id = id;
            session.State = stored.State;
            return session;
        }, token);

    public async Task<MockSession> UpsertRule(string id, string ruleId, MockRule rule, CancellationToken token) =>
        await Change(id, session =>
        {
            if (rule is null) throw new SessionException("Нужно правило");
            rule.Id = ruleId;
            var index = session.Rules.FindIndex(item => item.Id == ruleId);
            if (index < 0) session.Rules.Add(rule); else session.Rules[index] = rule;
            return session;
        }, token);

    public async Task DeleteRule(string id, string ruleId, CancellationToken token) =>
        await Change(id, session =>
        {
            if (session.Rules.RemoveAll(rule => rule.Id == ruleId) == 0) throw new SessionException("Правило не найдено", 404);
            return session;
        }, token);

    public async Task<MockSession> SetState(string id, string state, CancellationToken token) =>
        await Change(id, session => { SessionValidation.State(state); session.State = state; return session; }, token);

    public async Task<MockSession> Reset(string id, CancellationToken token)
    {
        using var lease = await locks.Enter(id, token);
        var row = await Find(id, token);
        var session = Decode(row);
        session.State = session.InitialState;
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        await database.SessionRequests.Where(request => request.SessionId == id).ExecuteDeleteAsync(token);
        row.Payload = Encode(session);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return session;
    }

    public async Task Delete(string id, CancellationToken token)
    {
        using var lease = await locks.Enter(id, token);
        database.Sessions.Remove(await Find(id, token));
        await database.SaveChangesAsync(token);
    }

    public async Task<object> Export(string id, CancellationToken token)
    {
        var session = await Get(id, token);
        return new { session.Name, session.UpstreamBaseUrl, session.ProxyEnabled, session.InitialState, session.Rules, session.ProtectedPaths };
    }

    public async Task<List<SessionRequest>> Requests(string id, CancellationToken token, int limit = 100, string? ruleId = null,
        string? method = null, int? statusCode = null, string? source = null, string? search = null,
        DateTime? from = null, DateTime? to = null, long? beforeId = null)
    {
        await Find(id, token);
        if (from > to) throw new SessionException("Начало периода должно быть раньше конца");
        var query = database.SessionRequests.AsNoTracking().Where(row => row.SessionId == id);
        if (!string.IsNullOrEmpty(ruleId)) query = query.Where(row => row.RuleId == ruleId);
        if (!string.IsNullOrEmpty(method)) query = query.Where(row => row.Method == method.ToUpperInvariant());
        if (statusCode is not null) query = query.Where(row => row.StatusCode == statusCode);
        if (!string.IsNullOrEmpty(source)) query = query.Where(row => row.Source == source);
        if (from is not null) query = query.Where(row => row.CreatedAt >= from.Value.ToUniversalTime());
        if (to is not null) query = query.Where(row => row.CreatedAt <= to.Value.ToUniversalTime());
        if (beforeId is not null) query = query.Where(row => row.Id < beforeId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.ToLowerInvariant();
            query = query.Where(row => row.Path.ToLower().Contains(term) || row.Reason.ToLower().Contains(term) ||
                (row.QueryString != null && row.QueryString.ToLower().Contains(term)) ||
                (row.RuleId != null && row.RuleId.ToLower().Contains(term)) ||
                (row.DetailsJson != null && row.DetailsJson.ToLower().Contains(term)));
        }
        return await query.OrderByDescending(row => row.Id).Take(Math.Clamp(limit, 1, 5000))
            .Select(row => new SessionRequest
            {
                Id = row.Id, SessionId = row.SessionId, CreatedAt = row.CreatedAt, Method = row.Method,
                Path = row.Path, QueryString = row.QueryString, RuleId = row.RuleId, Source = row.Source,
                StatusCode = row.StatusCode, DurationMs = row.DurationMs, Reason = row.Reason
            }).ToListAsync(token);
    }

    public async Task<SessionRequest> Request(string id, long requestId, CancellationToken token)
    {
        await Find(id, token);
        var request = await database.SessionRequests.AsNoTracking().SingleOrDefaultAsync(row => row.SessionId == id && row.Id == requestId, token)
            ?? throw new SessionException("Запись журнала не найдена", 404);
        if (request.DetailsJson is not null) request.Details = JsonSerializer.Deserialize<SessionRequestDetails>(request.DetailsJson, SessionJson.Options);
        return request;
    }

    public async Task<int> DeleteRequests(string id, JournalDeletion deletion, CancellationToken token)
    {
        if (deletion.All && deletion.Ids is { Length: > 0 } || !deletion.All && (deletion.Ids is not { Length: > 0 } || deletion.Ids.Length > 5000))
            throw new SessionException("Укажите all: true либо от 1 до 5000 id записей");
        using var lease = await locks.Enter(id, token);
        await Find(id, token);
        var query = database.SessionRequests.Where(row => row.SessionId == id);
        if (!deletion.All) query = query.Where(row => deletion.Ids!.Contains(row.Id));
        return await query.ExecuteDeleteAsync(token);
    }

    public async Task<VerificationResult> Verify(string id, RequestVerification verification, CancellationToken token)
    {
        if (verification.Count < 0) throw new SessionException("Количество не может быть отрицательным");
        await Find(id, token);
        var query = database.SessionRequests.AsNoTracking().Where(row => row.SessionId == id);
        if (verification.RuleId is not null) query = query.Where(row => row.RuleId == verification.RuleId);
        if (verification.Method is not null) query = query.Where(row => row.Method == verification.Method.ToUpperInvariant());
        if (verification.Path is not null) query = query.Where(row => row.Path == verification.Path);
        var count = await query.CountAsync(token);
        return new(count == verification.Count, count, verification.Count);
    }

    private async Task<MockSession> Change(string id, Func<MockSession, MockSession> change, CancellationToken token)
    {
        using var lease = await locks.Enter(id, token);
        var row = await Find(id, token);
        var session = change(Decode(row));
        SessionValidation.Validate(session);
        row.Payload = Encode(session);
        await database.SaveChangesAsync(token);
        return session;
    }

    private async Task<SessionRow> Find(string id, CancellationToken token) =>
        await database.Sessions.SingleOrDefaultAsync(row => row.Id == id, token) ?? throw new SessionException("Сессия не найдена", 404);
    internal static MockSession Decode(SessionRow row) => JsonSerializer.Deserialize<MockSession>(row.Payload, SessionJson.Options)!;
    internal static string Encode(MockSession session) => JsonSerializer.Serialize(session, SessionJson.Options);
}

public sealed record JournalDeletion(bool All = false, long[]? Ids = null);
