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
using static MockServer.Tests.TestResponses;

namespace MockServer.Tests;

public class SessionTests
{
    internal static object Rule(string path = "/v2/items/{itemId}/update", string id = "item-update", int status = 422) => new
    {
        id,
        match = new { method = "POST", path },
        response = new { status, json = new { message = "Тестовая ошибка" } }
    };

    internal static async Task<string> Create(HttpClient client, object? rules = null, string upstream = "")
    {
        var response = await client.PostAsJsonAsync("/admin/api/sessions", new { name = "Мобильная задача", upstreamBaseUrl = upstream, rules = rules ?? new[] { Rule() } });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (string)(await Read(response))["id"]!;
    }

    [Fact]
    public async Task Arbitrary_templates_are_isolated_and_persisted()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var first = await Create(client);
        var second = await Create(client, Array.Empty<object>());
        var reply = await client.PostAsync($"/s/{first}/v2/items/42/update", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, reply.StatusCode);
        Assert.Equal("Тестовая ошибка", (string?)(await Read(reply))["message"]);
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync($"/s/{second}/v2/items/42/update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/s/missing/whatever")).StatusCode);
        Assert.Single((await Read(await client.GetAsync($"/admin/api/sessions/{first}/requests"))).AsArray());
        using var restart = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>().WithWebHostBuilder(web => web.ConfigureAppConfiguration((_, configuration) =>
            Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(configuration, new Dictionary<string, string?> { ["DatabasePath"] = host.DatabasePath })));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await restart.CreateClient().PostAsync($"/s/{first}/v2/items/42/update", null)).StatusCode);
    }

    [Fact]
    public async Task Query_headers_json_and_form_conditions_select_rules()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await Create(client, new object[] {
            new { id = "json", match = new { method = "PATCH", path = "/anything", query = new { mode = "test" }, headers = new Dictionary<string,string> { ["X-Device"] = "mobile" }, json = new { item = new { id = 42 } } }, response = new { status = 201, json = new { ok = true } } },
            new { id = "form", match = new { method = "POST", path = "/forms/submit", query = new { mode = "test" }, form = new { itemId = "42" } }, response = new { status = 200, text = "form matched" } }
        });
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/s/{id}/anything?mode=test") { Content = JsonContent.Create(new { item = new { id = 42, extra = "value" } }) };
        request.Headers.Add("X-Device", "mobile");
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(request)).StatusCode);
        Assert.Equal("form matched", await (await client.PostAsync($"/s/{id}/forms/submit?mode=test", new FormUrlEncodedContent(new Dictionary<string, string> { ["itemId"] = "42" }))).Content.ReadAsStringAsync());
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("42"), "itemId");
        Assert.Equal("form matched", await (await client.PostAsync($"/s/{id}/forms/submit?mode=test", multipart)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync($"/s/{id}/forms/submit?mode=other", new FormUrlEncodedContent(new Dictionary<string, string> { ["itemId"] = "42" }))).StatusCode);
    }

    [Fact]
    public async Task Unmatched_requests_preserve_path_query_method_headers_and_inspected_body()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        upstream.Run(async context => await context.Response.WriteAsJsonAsync(new
        {
            path = context.Request.Path.Value,
            query = context.Request.QueryString.Value,
            method = context.Request.Method,
            rawTarget = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget,
            body = await new StreamReader(context.Request.Body).ReadToEndAsync(),
            device = context.Request.Headers["X-Device"].ToString()
        }));
        await upstream.StartAsync();
        var url = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var host = new TestHost();
        host.UseKestrel(0);
        using var client = host.CreateClient();
        var id = await Create(client, new[] { new { id = "inspect", match = new { method = "PUT", path = "/echo", json = new { match = true } }, response = new { text = "matched" } } }, url + "/base");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/s/{id}/echo?a=1&a=2&encoded=a%2Fb") { Content = new StringContent("{\"match\":false}", Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Device", "phone");
        var result = await Read(await client.SendAsync(request));
        Assert.Equal("/base/echo", (string?)result["path"]);
        Assert.Equal("?a=1&a=2&encoded=a%2Fb", (string?)result["query"]);
        Assert.Equal("PUT", (string?)result["method"]);
        Assert.Equal("{\"match\":false}", (string?)result["body"]);
        Assert.Equal("phone", (string?)result["device"]);
        var trailing = await Read(await client.GetAsync($"/s/{id}/folder/"));
        Assert.Equal("/base/folder/", (string?)trailing["path"]);
        var encoded = await Read(await client.GetAsync($"/s/{id}/files/a%2Fb"));
        Assert.Equal("/base/files/a%2Fb", (string?)encoded["path"]);
        var lowercase = await Read(await client.GetAsync($"/s/{id}/files/a%2fb"));
        Assert.Equal("/base/files/a%2fb", (string?)lowercase["rawTarget"]);
        foreach (var escaped in new[] { "/files/a%20b", "/files/a%25b", "/files/a%23b", "/files/a%3Fb" })
        {
            var forwarded = await Read(await client.GetAsync($"/s/{id}{escaped}"));
            Assert.Equal("/base" + escaped, (string?)forwarded["rawTarget"]);
        }
    }

    [Fact]
    public async Task State_transitions_reset_export_import_and_verification_work()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await Create(client, new object[] {
            new { id = "first", whenState = "started", nextState = "retried", match = new { method = "GET", path = "/retry" }, response = new { status = 503, text = "retry" } },
            new { id = "second", whenState = "retried", match = new { method = "GET", path = "/retry" }, response = new { status = 200, json = new { ok = true } } }
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/s/{id}/retry")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/s/{id}/retry")).StatusCode);
        var verify = await Read(await client.PostAsJsonAsync($"/admin/api/sessions/{id}/verify", new { ruleId = "first", count = 1 }));
        Assert.True((bool)verify["matched"]!);
        Assert.Equal(1, (int)verify["actualCount"]!);
        Assert.False((bool)(await Read(await client.PostAsJsonAsync($"/admin/api/sessions/{id}/verify", new { count = 0 })))["matched"]!);
        var export = await Read(await client.GetAsync($"/admin/api/sessions/{id}/export"));
        Assert.Null(export["id"]);
        var imported = await Read(await client.PostAsJsonAsync("/admin/api/sessions/import", export));
        Assert.NotEqual(id, (string?)imported["id"]);
        Assert.Equal("started", (string?)imported["state"]);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/admin/api/sessions/{id}/reset", null)).StatusCode);
        Assert.Empty((await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync($"/s/{id}/retry")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/admin/api/sessions/{id}/state", new { state = "retried" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/s/{id}/retry")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/api/sessions/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/s/{id}/retry")).StatusCode);
    }

    [Fact]
    public async Task Ambiguity_and_protected_paths_never_reach_upstream()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await Create(client, new[] { Rule("/orders/{id}"), Rule("/orders/42", "other") }, "http://127.0.0.1:1");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/s/{id}/orders/42", null)).StatusCode);
        var session = await Read(await client.GetAsync($"/admin/api/sessions/{id}"));
        session["protectedPaths"] = new JsonArray("/danger/{id}");
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/admin/api/sessions/{id}", session)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/s/{id}/danger/42")).StatusCode);
        var logs = (await Read(await client.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray();
        Assert.All(logs, row => Assert.Equal("blocked", (string?)row!["source"]));
    }

    [Fact]
    public async Task Priority_and_rule_updates_apply_without_restart()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await Create(client, new[] { Rule("/orders/{id}") });
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/admin/api/sessions/{id}/rules/specific", new { id = "specific", priority = 1, match = new { path = "/orders/42", method = "POST" }, response = new { status = 201 } })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsync($"/s/{id}/orders/42", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/admin/api/sessions/{id}/rules/specific")).StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.PostAsync($"/s/{id}/orders/42", null)).StatusCode);
    }

    [Theory]
    [InlineData("{\"name\":\"bad\",\"rules\":null}")]
    [InlineData("{\"name\":\"bad\",\"upstreamBaseUrl\":\"ftp://example.com\"}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":{\"path\":\"relative\"}}]}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":{\"path\":\"/ok\"},\"response\":{\"delayMs\":30001}}]}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":{\"path\":\"/ok\"},\"response\":{\"headers\":{\"Content-Length\":\"900\"}}}]}")]
    public async Task Invalid_configurations_are_rejected_atomically(string json)
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/api/sessions", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Empty((await Read(await client.GetAsync("/admin/api/sessions"))).AsArray());
    }

    [Fact]
    public async Task Parallel_requests_reserve_state_transitions_once()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await Create(client, new object[] {
            new { id = "once", whenState = "started", nextState = "done", match = new { path = "/once" }, response = new { status = 201 } },
            new { id = "after", whenState = "done", match = new { path = "/once" }, response = new { status = 200 } }
        });
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync($"/s/{id}/once")));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
    }
}
