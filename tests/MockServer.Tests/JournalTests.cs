using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MockServer.Infrastructure;
using System.IO.Compression;
using static MockServer.Tests.TestResponses;

namespace MockServer.Tests;

public class JournalTests
{
    [Fact]
    public async Task Details_capture_both_sides_and_mask_nested_secrets_without_changing_traffic()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new {
            id = "capture", match = new { path = "/capture" }, nextState = "done",
            response = new { json = new { result = "ok", access_token = "response-secret" },
                headers = new Dictionary<string, string> { ["Set-Cookie"] = "sid=response-cookie", ["X-Result"] = "success" } }
        } });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/s/{id}/capture?tag=a&tag=b&api_key=query-secret")
        {
            Content = JsonContent.Create(new { name = "visible", nested = new { password = "body-secret" } })
        };
        request.Headers.Add("Authorization", "Bearer header-secret");
        request.Headers.Add("X-Device", "phone");
        var response = await client.SendAsync(request);
        Assert.Contains("response-secret", await response.Content.ReadAsStringAsync());
        var list = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray();
        var row = Assert.Single(list)!;
        var detailResponse = await client.GetAsync($"/admin/api/sessions/{id}/requests/{row["id"]}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await Read(detailResponse);
        var raw = detail.ToJsonString();
        foreach (var secret in new[] { "response-secret", "response-cookie", "query-secret", "body-secret", "header-secret" })
            Assert.DoesNotContain(secret, raw);
        Assert.Contains("visible", raw);
        Assert.Contains("phone", raw);
        Assert.Contains("success", raw);
        Assert.Contains("tag=a&tag=b", (string?)row["queryString"]);
        Assert.Equal("started", (string?)detail["details"]!["stateBefore"]);
        Assert.Equal("done", (string?)detail["details"]!["stateAfter"]);
        Assert.Equal("json", (string?)detail["details"]!["responseBody"]!["encoding"]);
        Assert.Null(row["details"]);
    }

    [Fact]
    public async Task Journal_filters_before_paging_and_deletes_only_requested_session_rows()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "capture", match = new { path = "/capture" }, response = new { status = 201, text = "needle" } } });
        var other = await SessionTests.Create(client);
        await client.PutAsJsonAsync($"/admin/api/sessions/{id}/state", new { state = "checked" });
        await client.PostAsync($"/s/{other}/v2/items/42/update", null);
        await client.PostAsync($"/s/{id}/capture?find=me", null);
        await client.GetAsync($"/s/{id}/capture");
        await client.GetAsync($"/s/{id}/missing");
        var filtered = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests?method=POST&statusCode=201&source=mock&search=needle&limit=1"))).AsArray();
        var first = Assert.Single(filtered)!;
        Assert.Equal("POST", (string?)first["method"]);
        var all = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests?limit=2"))).AsArray();
        Assert.Equal(2, all.Count);
        var next = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests?limit=2&beforeId={all.Last()!["id"]}"))).AsArray();
        Assert.Single(next);
        Assert.Equal(first["id"]!.ToString(), next[0]!["id"]!.ToString());
        Assert.Empty((await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests?from=2099-01-01T00:00:00Z"))).AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/admin/api/sessions/{id}/requests?from=2027-01-01&to=2026-01-01")).StatusCode);
        var foreign = (await Read(await client.GetAsync($"/admin/api/sessions/{other}/requests"))).AsArray()[0]!["id"]!.GetValue<long>();
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/sessions/{id}/requests")
        { Content = JsonContent.Create(new { ids = new[] { first["id"]!.GetValue<long>(), foreign } }) };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(delete)).StatusCode);
        Assert.Single((await Read(await client.GetAsync($"/admin/api/sessions/{other}/requests"))).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/admin/api/sessions/{id}/requests/{foreign}")).StatusCode);
        using var clear = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/sessions/{id}/requests")
        { Content = JsonContent.Create(new { all = true }) };
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(clear)).StatusCode);
        Assert.Empty((await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray());
        Assert.Equal("checked", (string?)(await Read(await client.GetAsync($"/admin/api/sessions/{id}")))["state"]);
        using var empty = new HttpRequestMessage(HttpMethod.Delete, $"/admin/api/sessions/{id}/requests")
        { Content = JsonContent.Create(new { ids = Array.Empty<long>() }) };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(empty)).StatusCode);
    }

    [Fact]
    public async Task More_than_one_hundred_records_remain_accessible()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "ok", match = new { path = "/ok" }, response = new { status = 204 } } });
        for (var index = 0; index < 105; index++) await client.GetAsync($"/s/{id}/ok");
        Assert.Equal(105, (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests?limit=200"))).AsArray().Count);
    }

    [Fact]
    public async Task New_requests_do_not_prune_existing_history_after_five_thousand_rows()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<MockDbContext>();
            database.SessionRequests.AddRange(Enumerable.Range(0, 5001).Select(index => new SessionRequest { SessionId = id, Path = "/preserved", Method = "GET" }));
            await database.SaveChangesAsync();
        }
        await client.PostAsync($"/s/{id}/v2/items/42/update", null);
        var verification = await Read(await client.PostAsJsonAsync($"/admin/api/sessions/{id}/verify", new { count = 5002 }));
        Assert.True((bool)verification["matched"]!);
    }

    [Fact]
    public async Task Multipart_fields_and_xml_are_captured_with_masked_values()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "ok", match = new { path = "/data" }, response = new { text = "ok" } } });
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("visible-form"), "name");
        form.Add(new StringContent("form-secret"), "password");
        form.Add(new ByteArrayContent(new byte[] { 0, 1, 2 }), "upload", "file.bin");
        await client.PostAsync($"/s/{id}/data", form);
        var first = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray()[0]!;
        var multipart = await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests/{first["id"]}"));
        Assert.Contains("visible-form", multipart.ToJsonString());
        Assert.Contains("file.bin", multipart.ToJsonString());
        Assert.DoesNotContain("form-secret", multipart.ToJsonString());
        await client.PostAsync($"/s/{id}/data", new StringContent("<data><name>visible-xml</name><password>xml-secret</password><user token=\"attribute-secret\" /></data>", Encoding.UTF8, "application/xml"));
        var second = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray()[0]!;
        var xml = await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests/{second["id"]}"));
        Assert.Contains("visible-xml", xml.ToJsonString());
        Assert.DoesNotContain("xml-secret", xml.ToJsonString());
        Assert.DoesNotContain("attribute-secret", xml.ToJsonString());
    }

    [Theory]
    [InlineData("application/json", "{\"a\":1,\"a\":2}")]
    [InlineData("multipart/mixed; boundary=batch", "--batch\r\nContent-Type: text/plain\r\n\r\nhello\r\n--batch--\r\n")]
    [InlineData("text/plain; charset=utf-7", "hello")]
    public async Task Unsupported_capture_formats_never_break_a_matched_response(string contentType, string body)
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "ok", match = new { path = "/data" }, response = new { text = "ok" } } });
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        var response = await client.PostAsync($"/s/{id}/data", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        Assert.Single((await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray());
    }

    [Fact]
    public async Task Compressed_json_is_redacted_before_persistence()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "ok", match = new { path = "/data" }, response = new { text = "ok" } } });
        using var buffer = new MemoryStream();
        await using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, true))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("{\"password\":\"gzip-secret\",\"name\":\"visible-gzip\"}"));
        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new("application/json");
        content.Headers.ContentEncoding.Add("gzip");
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/s/{id}/data", content)).StatusCode);
        var row = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray()[0]!;
        var details = await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests/{row["id"]}"));
        var body = details["details"]!["requestBody"]!;
        Assert.Equal("json", (string?)body["encoding"]);
        Assert.Contains("visible-gzip", (string?)body["content"]);
        Assert.DoesNotContain("gzip-secret", details.ToJsonString());
    }

    [Fact]
    public async Task Journal_search_finds_cyrillic_body_values()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "ok", match = new { path = "/data" }, response = new { json = new { message = "Ошибка проверки" } } } });
        await client.GetAsync($"/s/{id}/data");
        var rows = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests?search={Uri.EscapeDataString("оШиБка")}"))).AsArray();
        Assert.Single(rows);
    }

    [Fact]
    public async Task Proxy_starts_receiving_before_a_streaming_upload_finishes()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.Run(async context =>
        {
            var first = new byte[1];
            await context.Request.Body.ReadExactlyAsync(first);
            arrived.TrySetResult();
            await context.Request.Body.CopyToAsync(Stream.Null);
            await context.Response.WriteAsync("done");
        });
        await upstream.StartAsync();
        var url = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var host = new TestHost();
        host.UseKestrel(0);
        using var startupClient = host.CreateClient();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var id = await SessionTests.Create(client, Array.Empty<object>(), url);
        var pending = client.PostAsync($"/s/{id}/stream", new StreamingContent(release.Task));
        try { await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { release.TrySetResult(); }
        Assert.Equal("done", await (await pending).Content.ReadAsStringAsync());
    }

    private sealed class StreamingContent(Task release) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[16384]);
            await stream.FlushAsync();
            await release;
            await stream.WriteAsync(new byte[] { 2, 3 });
        }
    }

    [Fact]
    public async Task Truncated_json_is_not_stored_unredacted_and_binary_response_is_base64()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "ok", match = new { path = "/ok" }, response = new { text = "ok" } } });
        var body = "{\"password\":\"never-store\",\"large\":\"" + new string('x', 300000) + "\"}";
        Assert.Equal("ok", await (await client.PostAsync($"/s/{id}/ok", new StringContent(body, Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync());
        var row = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray()[0]!;
        var details = await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests/{row["id"]}"));
        Assert.DoesNotContain("never-store", details.ToJsonString());
        Assert.True((bool)details["details"]!["requestBody"]!["truncated"]!);
    }

    [Fact]
    public async Task Proxy_capture_preserves_payload_and_streamed_response()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.Run(async context =>
        {
            Assert.Equal("hello upstream", await new StreamReader(context.Request.Body).ReadToEndAsync());
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.WriteAsync(new byte[] { 0, 1, 2, 255 });
        });
        await upstream.StartAsync();
        var url = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var host = new TestHost();
        host.UseKestrel(0);
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, Array.Empty<object>(), url);
        Assert.Equal(new byte[] { 0, 1, 2, 255 }, await (await client.PostAsync($"/s/{id}/binary", new StringContent("hello upstream"))).Content.ReadAsByteArrayAsync());
        var rows = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray();
        var row = Assert.Single(rows)!;
        var details = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests/{row["id"]}")))["details"]!;
        Assert.Equal("hello upstream", (string?)details["requestBody"]!["content"]);
        Assert.Equal("AAEC/w==", (string?)details["responseBody"]!["content"]);
        Assert.Equal(4, (long)details["responseBody"]!["size"]!);
        Assert.Contains(url, (string?)details["upstreamUrl"]);
    }
}
