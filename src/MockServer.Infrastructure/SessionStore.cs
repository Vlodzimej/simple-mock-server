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

    public async Task<List<SessionRequest>> Requests(string id, CancellationToken token, int limit = 100, string? ruleId = null)
    {
        await Find(id, token);
        var query = database.SessionRequests.AsNoTracking().Where(row => row.SessionId == id);
        if (ruleId is not null) query = query.Where(row => row.RuleId == ruleId);
        return await query.OrderByDescending(row => row.Id).Take(Math.Clamp(limit, 1, 5000)).ToListAsync(token);
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
