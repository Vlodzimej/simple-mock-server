using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using MockServer.Core;
using MockServer.Infrastructure;

namespace MockServer.Api;

[McpServerToolType]
public sealed class SessionTools(SessionStore store)
{
    [McpServerTool(Name = "create_session", Destructive = false, OpenWorld = false), Description("Создание изолированной HTTP-сессии. configuration: name, upstreamBaseUrl, proxyEnabled, initialState, rules, protectedPaths. Возвращает id и basePath /s/{id}; адрес устройства складывается из адреса сервера и basePath. Новые пути задаются правилами без изменения кода.")]
    public Task<CallToolResult> CreateSession(MockSession configuration, CancellationToken cancellationToken) => Run(async () =>
    {
        var session = await store.Create(configuration, cancellationToken);
        return new { session.Id, session.Name, basePath = $"/s/{session.Id}" };
    });

    [McpServerTool(Name = "list_sessions", ReadOnly = true, OpenWorld = false), Description("Список HTTP-сессий с настройками, правилами и текущим состоянием.")]
    public Task<CallToolResult> ListSessions(CancellationToken cancellationToken) => Run(async () => await store.List(cancellationToken));

    [McpServerTool(Name = "get_session", ReadOnly = true, OpenWorld = false), Description("Получение конфигурации и состояния HTTP-сессии по sessionId.")]
    public Task<CallToolResult> GetSession(string sessionId, CancellationToken cancellationToken) => Run(async () => await store.Get(sessionId, cancellationToken));

    [McpServerTool(Name = "update_session", OpenWorld = false), Description("Полная замена конфигурации HTTP-сессии; текущее состояние сохраняется. Для изменения одного правила используйте upsert_rule.")]
    public Task<CallToolResult> UpdateSession(string sessionId, MockSession configuration, CancellationToken cancellationToken) => Run(async () => await store.Update(sessionId, configuration, cancellationToken));

    [McpServerTool(Name = "delete_session", OpenWorld = false), Description("Удаление HTTP-сессии, её правил и журнала. Адрес сессии перестаёт обслуживать запросы.")]
    public Task<CallToolResult> DeleteSession(string sessionId, CancellationToken cancellationToken) => Run(async () =>
    {
        await store.Delete(sessionId, cancellationToken);
        return new { deleted = true };
    });

    [McpServerTool(Name = "upsert_rule", OpenWorld = false), Description("Создание или замена одного правила. rule.match: path (точный или /orders/{id}), method (* по умолчанию), query, headers, form, json (подмножество объекта). rule.response: status, delayMs, headers, json либо text. Меньший priority выигрывает (по умолчанию 100); равный лучший приоритет даёт 409. whenState/nextState задают последовательность ответов.")]
    public Task<CallToolResult> UpsertRule(string sessionId, string ruleId, MockRule rule, CancellationToken cancellationToken) => Run(async () => await store.UpsertRule(sessionId, ruleId, rule, cancellationToken));

    [McpServerTool(Name = "delete_rule", OpenWorld = false), Description("Удаление правила. После удаления запрос может попасть на upstream, если путь не закреплён в protectedPaths.")]
    public Task<CallToolResult> DeleteRule(string sessionId, string ruleId, CancellationToken cancellationToken) => Run(async () =>
    {
        await store.DeleteRule(sessionId, ruleId, cancellationToken);
        return new { deleted = true };
    });

    [McpServerTool(Name = "set_scenario_state", OpenWorld = false), Description("Установка именованного состояния сценария для правил whenState. Изоляция по sessionId.")]
    public Task<CallToolResult> SetState(string sessionId, string state, CancellationToken cancellationToken) => Run(async () => await store.SetState(sessionId, state, cancellationToken));

    [McpServerTool(Name = "list_requests", ReadOnly = true, OpenWorld = false), Description("Последние запросы сессии: путь, метод, ruleId, источник mock/proxy/blocked, HTTP-статус и причина. По умолчанию 100, максимум 5000; тела и секретные заголовки не сохраняются.")]
    public Task<CallToolResult> ListRequests(string sessionId, CancellationToken cancellationToken, int limit = 100, string? ruleId = null) => Run(async () => await store.Requests(sessionId, cancellationToken, limit, ruleId));

    [McpServerTool(Name = "verify_requests", ReadOnly = true, OpenWorld = false), Description("Проверка точного количества запросов в сохранённом журнале. verification: count (по умолчанию 1), необязательные ruleId/method/path. matched означает равенство actualCount и expectedCount. Журнал ограничен 5000 запросами на сессию.")]
    public Task<CallToolResult> VerifyRequests(string sessionId, RequestVerification verification, CancellationToken cancellationToken) => Run(async () => await store.Verify(sessionId, verification, cancellationToken));

    [McpServerTool(Name = "reset_session", OpenWorld = false), Description("Очистка журнала и возврат в initialState; конфигурация и правила сохраняются.")]
    public Task<CallToolResult> ResetSession(string sessionId, CancellationToken cancellationToken) => Run(async () => await store.Reset(sessionId, cancellationToken));

    [McpServerTool(Name = "export_session", ReadOnly = true, OpenWorld = false), Description("Экспорт переносимой конфигурации без id, текущего состояния и журнала. Результат можно передать в import_session.")]
    public Task<CallToolResult> ExportSession(string sessionId, CancellationToken cancellationToken) => Run(() => store.Export(sessionId, cancellationToken));

    [McpServerTool(Name = "import_session", Destructive = false, OpenWorld = false), Description("Импорт конфигурации как новой сессии с новым id и initialState. Существующие сессии не изменяются.")]
    public Task<CallToolResult> ImportSession(MockSession configuration, CancellationToken cancellationToken) => Run(async () => await store.Create(configuration, cancellationToken));

    private static async Task<CallToolResult> Run(Func<Task<object>> operation)
    {
        try
        {
            var value = await operation();
            var json = JsonSerializer.SerializeToElement(value, SessionJson.Options);
            if (json.ValueKind == JsonValueKind.Array) json = JsonSerializer.SerializeToElement(new { items = value }, SessionJson.Options);
            return new() { Content = [new TextContentBlock { Text = json.GetRawText() }], StructuredContent = json };
        }
        catch (SessionException exception)
        {
            return new() { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(new { message = exception.Message, status = exception.Status }, SessionJson.Options) }] };
        }
    }
}
