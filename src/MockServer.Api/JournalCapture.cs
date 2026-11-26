using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.IO.Compression;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using MockServer.Infrastructure;

namespace MockServer.Api;

internal static class JournalCapture
{
    internal const int BodyLimit = 262144;
    private const string Mask = "[REDACTED]";

    private static bool Secret(string key)
    {
        var name = new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return name.Contains("password") || name.Contains("passwd") || name.Contains("secret") ||
            name.Contains("token") || name.Contains("authorization") || name.Contains("cookie") ||
            name.Contains("apikey") || name.Contains("credential") || name is "pwd" or "sessionid";
    }

    internal static string Query(string query)
    {
        if (string.IsNullOrEmpty(query)) return "";
        var values = QueryHelpers.ParseQuery(query);
        return QueryString.Create(values.SelectMany(pair => pair.Value.Select(value =>
            new KeyValuePair<string, string?>(pair.Key, Secret(pair.Key) ? Mask : value)))).Value ?? "";
    }

    internal static string Url(string url)
    {
        var query = url.IndexOf('?');
        var fragment = url.IndexOf('#');
        var end = fragment < 0 ? url.Length : fragment;
        var prefix = query < 0 ? url[..end] : url[..query];
        if (Uri.TryCreate(prefix, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            prefix = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.GetLeftPart(UriPartial.Path);
        return prefix + (query >= 0 && query < end ? Query(url[query..end]) : "");
    }

    internal static Dictionary<string, string[]> Headers(IHeaderDictionary headers) => headers.ToDictionary(
        pair => pair.Key,
        pair => Secret(pair.Key) ? new[] { Mask } : pair.Value.Select(value =>
            pair.Key.Equals("Location", StringComparison.OrdinalIgnoreCase) || pair.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                ? Url(value ?? "") : Text(value ?? "")).ToArray());

    internal static async Task<JournalBody> Request(HttpRequest request, JournalRequestStream capture, bool readRemaining, CancellationToken token)
    {
        if (readRemaining && request.ContentLength is > 0 && !token.IsCancellationRequested)
            await capture.ReadPrefix(token);
        var truncated = capture.Size > BodyLimit || request.ContentLength > BodyLimit;
        var body = await Body(capture.Bytes, request.ContentType,
            request.ContentLength ?? (capture.EndOfStream ? capture.Size : null), truncated, request.Headers.ContentEncoding.ToString());
        if (!capture.EndOfStream && capture.Size < (request.ContentLength ?? long.MaxValue) && capture.Size < BodyLimit)
            body = body with { Note = "Сохранена только прочитанная часть запроса. " + body.Note };
        return body;
    }

    internal static async Task<JournalBody> Body(byte[] bytes, string? contentType, long? size, bool truncated, string? contentEncoding = null)
    {
        var type = contentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "";
        if (bytes.Length == 0) return new("", "text", contentType, size, truncated);
        if (!string.IsNullOrEmpty(contentEncoding) && !contentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        {
            if (truncated) return new("", "omitted", contentType, size, true, "Усечённое сжатое тело не сохранено.");
            try
            {
                using var source = new MemoryStream(bytes, false);
                using Stream? decoder = contentEncoding.ToLowerInvariant() switch
                {
                    "gzip" => new GZipStream(source, CompressionMode.Decompress),
                    "br" => new BrotliStream(source, CompressionMode.Decompress),
                    "deflate" => new ZLibStream(source, CompressionMode.Decompress),
                    _ => null
                };
                if (decoder is null) return new("", "omitted", contentType, size, false, "Кодирование тела не поддерживается для безопасного маскирования.");
                var expanded = new byte[BodyLimit + 1];
                var count = 0;
                while (count < expanded.Length)
                {
                    var read = await decoder.ReadAsync(expanded.AsMemory(count));
                    if (read == 0) break;
                    count += read;
                }
                var body = await Body(expanded.AsSpan(0, Math.Min(count, BodyLimit)).ToArray(), contentType, size, count > BodyLimit);
                return body with { Note = $"Распаковано из {contentEncoding}. Размер указан до распаковки. " + body.Note };
            }
            catch (InvalidDataException) { return new("", "omitted", contentType, size, false, "Не удалось распаковать тело для маскирования."); }
        }
        if (type.StartsWith("multipart/"))
        {
            if (type != "multipart/form-data") return new("", "omitted", contentType, size, truncated, "Этот тип multipart не поддерживается для безопасного маскирования.");
            if (truncated) return new("", "omitted", contentType, size, true, "Усечённый multipart не сохранён: безопасный разбор невозможен.");
            try
            {
                var context = new DefaultHttpContext();
                context.Request.ContentType = contentType;
                context.Request.Body = new MemoryStream(bytes, false);
                var form = await context.Request.ReadFormAsync();
                var fields = form.ToDictionary(pair => pair.Key, pair => Secret(pair.Key) ? new[] { Mask } : pair.Value.Select(value => Text(value ?? "")).ToArray());
                var files = form.Files.Select(file => new { name = Text(file.Name), fileName = Text(file.FileName), contentType = file.ContentType, size = file.Length }).ToArray();
                return Limited(JsonSerializer.Serialize(new { fields, files }, SessionRequestDetails.JsonOptions), "multipart", contentType, size, false,
                    "Для файлов сохраняются имя, тип и размер; содержимое файлов не сохраняется.");
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException or InvalidOperationException or ArgumentException)
            { return new("", "omitted", contentType, size, false, "Некорректный multipart не сохранён."); }
        }
        var textual = type.StartsWith("text/") || type.Contains("json") || type.Contains("xml") ||
            type is "application/x-www-form-urlencoded" or "application/javascript";
        if (!textual) return new(Convert.ToBase64String(bytes), "base64", contentType, size, truncated);
        Encoding encoding = Encoding.UTF8;
        if (MediaTypeHeaderValue.TryParse(contentType, out var media) && media.Charset.HasValue)
        {
            try { encoding = Encoding.GetEncoding(media.Charset.Value!.Trim('"')); }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            { return new("", "omitted", contentType, size, truncated, "Кодировка текста не поддерживается для безопасного маскирования."); }
        }
        var text = encoding.GetString(bytes);
        if (type.Contains("json"))
        {
            if (truncated) return new("", "omitted", contentType, size, true, "Усечённый JSON не сохранён: безопасное маскирование невозможно.");
            try
            {
                var node = JsonNode.Parse(text);
                node = Redact(node);
                return Limited(node?.ToJsonString(SessionRequestDetails.JsonOptions) ?? "null", "json", contentType, size, false);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
            { return new("", "omitted", contentType, size, false, "Некорректный JSON не сохранён: безопасное маскирование невозможно."); }
        }
        if (type == "application/x-www-form-urlencoded")
            return Limited(Query("?" + text).TrimStart('?'), "form", contentType, size, truncated);
        if (type.Contains("xml"))
        {
            if (truncated) return new("", "omitted", contentType, size, true, "Усечённый XML не сохранён: безопасное маскирование невозможно.");
            try
            {
                using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var document = XDocument.Load(reader);
                foreach (var element in document.Descendants().ToArray())
                {
                    foreach (var attribute in element.Attributes())
                        attribute.Value = Secret(attribute.Name.LocalName) ? Mask : Text(attribute.Value);
                    if (Secret(element.Name.LocalName)) element.Value = Mask;
                    else foreach (var value in element.Nodes().OfType<XText>()) value.Value = Text(value.Value);
                }
                return Limited(document.ToString(), "xml", contentType, size, false);
            }
            catch (XmlException) { return new("", "omitted", contentType, size, false, "Некорректный XML не сохранён."); }
        }
        return Limited(Text(text), "text", contentType, size, truncated);
    }

    private static JournalBody Limited(string content, string encoding, string? contentType, long? size, bool truncated, string? note = null) =>
        Encoding.UTF8.GetByteCount(content) <= BodyLimit
            ? new(content, encoding, contentType, size, truncated, note)
            : new("", "omitted", contentType, size, true, "После маскирования тело превышает 256 KiB.");

    private static JsonNode? Redact(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if (Secret(key)) obj[key] = Mask;
                else obj[key] = Redact(obj[key]);
            }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++) array[index] = Redact(array[index]);
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
            return JsonValue.Create(Text(text));
        return node;
    }

    internal static string Text(string text) => Regex.Replace(text,
        @"(?i)(\b(?:bearer|basic)\s+)[A-Za-z0-9+/=._~\-]+|((?:password|passwd|pwd|[\w-]*token|[\w-]*secret|api[_-]?key|authorization|cookie)\s*[=:]\s*)[^\s&,;]+",
        match => (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) + Mask,
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromSeconds(1));
}

internal sealed class JournalRequestStream(Stream source) : Stream
{
    private readonly MemoryStream captured = new();
    internal long Size { get; private set; }
    internal bool EndOfStream { get; private set; }
    internal byte[] Bytes => captured.ToArray();
    private void Capture(long position, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) EndOfStream = true;
        Size = Math.Max(Size, position + bytes.Length);
        if (position >= JournalCapture.BodyLimit) return;
        captured.Position = position;
        captured.Write(bytes[..Math.Min(JournalCapture.BodyLimit - (int)position, bytes.Length)]);
    }
    internal async Task ReadPrefix(CancellationToken token)
    {
        if (Size > JournalCapture.BodyLimit || EndOfStream) return;
        var position = Position;
        var buffer = new byte[8192];
        try
        {
            Position = Size;
            while (Position <= JournalCapture.BodyLimit)
            {
                var count = await ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, JournalCapture.BodyLimit + 1 - (int)Position)), token);
                if (count == 0) break;
            }
        }
        finally { Position = position; }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var position = Position;
        var read = source.Read(buffer, offset, count);
        Capture(position, buffer.AsSpan(offset, read));
        return read;
    }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
        await ReadAsync(buffer.AsMemory(offset, count), token);
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
    {
        var position = Position;
        var read = await source.ReadAsync(buffer, token);
        Capture(position, buffer.Span[..read]);
        return read;
    }
    public override bool CanRead => true;
    public override bool CanSeek => source.CanSeek;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => source.Position; set => source.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
    public override void Flush() => source.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) captured.Dispose(); base.Dispose(disposing); }
}

internal sealed class JournalResponseStream(Stream destination) : Stream
{
    private readonly MemoryStream captured = new();
    internal long Size { get; private set; }
    internal byte[] Bytes => captured.ToArray();
    private void Capture(ReadOnlySpan<byte> bytes)
    {
        Size += bytes.Length;
        var remaining = JournalCapture.BodyLimit - (int)captured.Length;
        if (remaining > 0) captured.Write(bytes[..Math.Min(remaining, bytes.Length)]);
    }
    public override void Write(byte[] buffer, int offset, int count)
    { destination.Write(buffer, offset, count); Capture(buffer.AsSpan(offset, count)); }
    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken token)
    { await destination.WriteAsync(buffer.AsMemory(offset, count), token); Capture(buffer.AsSpan(offset, count)); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
    { await destination.WriteAsync(buffer, token); Capture(buffer.Span); }
    public override void Flush() => destination.Flush();
    public override Task FlushAsync(CancellationToken token) => destination.FlushAsync(token);
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) captured.Dispose(); base.Dispose(disposing); }
}
