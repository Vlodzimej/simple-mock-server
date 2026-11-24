using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MockServer.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MockSession
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string UpstreamBaseUrl { get; set; } = "";
    public bool ProxyEnabled { get; set; } = true;
    public string InitialState { get; set; } = "started";
    public string State { get; set; } = "started";
    public List<MockRule> Rules { get; set; } = [];
    public List<string> ProtectedPaths { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class MockRule
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 100;
    public RequestMatch Match { get; set; } = new();
    public string? WhenState { get; set; }
    public string? NextState { get; set; }
    public RuleResponse Response { get; set; } = new();
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RequestMatch
{
    public string Method { get; set; } = "*";
    public string Path { get; set; } = "";
    public Dictionary<string, string> Query { get; set; } = [];
    public Dictionary<string, string> Headers { get; set; } = [];
    public Dictionary<string, string> Form { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Json { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RuleResponse
{
    public int Status { get; set; } = 200;
    public int DelayMs { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public JsonElement Json { get; set; }
    public string? Text { get; set; }
}

public sealed record RequestVerification(string? RuleId = null, string? Method = null, string? Path = null, int Count = 1);
public sealed record VerificationResult(bool Matched, int ActualCount, int ExpectedCount);

public static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
}

public sealed class SessionException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public static class SessionValidation
{
    private static readonly Regex Identifier = new("^[A-Za-z0-9][A-Za-z0-9_-]{0,79}$", RegexOptions.CultureInvariant);
    private static readonly Regex Token = new("^[!#$%&'*+.^_`|~0-9A-Za-z-]+$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> UnsafeHeaders = new(["Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length", "Host"], StringComparer.OrdinalIgnoreCase);

    public static void Validate(MockSession session)
    {
        if (string.IsNullOrWhiteSpace(session.Name) || session.Name.Length > 200) Fail("Название сессии: от 1 до 200 символов");
        if (session.UpstreamBaseUrl is null || session.UpstreamBaseUrl.Length > 2000) Fail("Некорректный upstreamBaseUrl");
        if (session.UpstreamBaseUrl!.Length > 0 && (!Uri.TryCreate(session.UpstreamBaseUrl, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0))
            Fail("Нужен HTTP(S) upstreamBaseUrl без логина, query и fragment");
        State(session.InitialState);
        State(session.State);
        if (session.Rules is null || session.Rules.Count > 100 || session.ProtectedPaths is null || session.ProtectedPaths.Count > 100)
            Fail("Допускается до 100 правил и защищённых путей; списки не могут быть null");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in session.Rules!)
        {
            if (rule is null || rule.Id is null || !Identifier.IsMatch(rule.Id) || !ids.Add(rule.Id)) Fail("Нужны уникальные id правил: буквы, цифры, _ и -; до 80 символов");
            if (rule!.Match is null || rule.Response is null) Fail("Нужны match и response");
            var match = rule.Match!;
            Path(match.Path);
            if (match.Method is null || match.Method.Length > 32 || (match.Method != "*" && !Token.IsMatch(match.Method))) Fail("Некорректный HTTP-метод");
            Fields(match.Query);
            Fields(match.Headers, true);
            Fields(match.Form);
            if (rule.WhenState is not null) State(rule.WhenState);
            if (rule.NextState is not null) State(rule.NextState);
            var response = rule.Response!;
            if (response.Status is < 200 or > 599 || response.DelayMs is < 0 or > 30000) Fail("Статус: 200–599; задержка: 0–30000 мс");
            Fields(response.Headers, true);
            if (response.Headers.Keys.Any(UnsafeHeaders.Contains)) Fail("Служебные HTTP-заголовки ответа задаются сервером");
            if (response.Json.ValueKind != JsonValueKind.Undefined && response.Text is not null) Fail("Укажите json либо text в ответе");
            if (response.Status is 204 or 205 or 304 && (response.Json.ValueKind != JsonValueKind.Undefined || response.Text is not null)) Fail("Этот HTTP-статус не допускает тело ответа");
            if (response.Text?.Length > 262144 || (response.Json.ValueKind != JsonValueKind.Undefined && response.Json.GetRawText().Length > 262144) ||
                (match.Json.ValueKind != JsonValueKind.Undefined && match.Json.GetRawText().Length > 65536)) Fail("Превышен размер данных правила");
        }
        foreach (var path in session.ProtectedPaths!) Path(path);
        if (JsonSerializer.Serialize(session, SessionJson.Options).Length > 524288) Fail("Размер конфигурации превышает 512 KiB");
    }

    public static void State(string? state)
    {
        if (string.IsNullOrWhiteSpace(state) || state.Length > 100) Fail("Состояние: от 1 до 100 символов");
    }

    public static void Path(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 2000 || !path.StartsWith('/') || path.StartsWith("//") || path.IndexOfAny(['?', '#', '\\', '\r', '\n']) >= 0) Fail("Нужен путь от / без query и fragment");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var segment in path!.Split('/'))
        {
            if (segment is "." or "..") Fail("Сегменты . и .. не поддерживаются");
            if (!segment.Contains('{') && !segment.Contains('}')) continue;
            if (segment.Length < 3 || segment[0] != '{' || segment[^1] != '}' || !Identifier.IsMatch(segment[1..^1]) || !names.Add(segment[1..^1]))
                Fail("Параметр пути: отдельный сегмент {name} с уникальным именем");
        }
    }

    private static void Fields(Dictionary<string, string>? fields, bool headers = false)
    {
        if (fields is null || fields.Count > 100) Fail("Условия и заголовки: до 100 полей, объект не может быть null");
        foreach (var (key, value) in fields!)
            if (key.Length is 0 or > 200 || value is null || value.Length > 65536 || (headers && (!Token.IsMatch(key) || value.Any(c => c < 32 && c != '\t' || c == 127))))
                Fail("Некорректное поле или HTTP-заголовок");
        if (headers && fields!.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Count) Fail("Повторяющиеся HTTP-заголовки");
    }

    private static void Fail(string message) => throw new SessionException(message);
}
