using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static MockServer.Tests.TestResponses;

namespace MockServer.Tests;

public class SessionBoundaryTests
{
    [Fact]
    public async Task Literal_json_null_is_distinct_from_an_absent_condition_or_body()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var configuration = """{"name":"null JSON","rules":[{"id":"null","match":{"path":"/null","json":null},"response":{"json":null}}]}""";
        var created = await Read(await client.PostAsync("/admin/api/sessions", new StringContent(configuration, Encoding.UTF8, "application/json")));
        var id = (string)created["id"]!;
        Assert.Equal("null", await (await client.PostAsync($"/s/{id}/null", new StringContent("null", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsync($"/s/{id}/null", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync($"/s/{id}/null")).StatusCode);
    }

    [Theory]
    [InlineData("{\"name\":\"bad\",\"rules\":[null]}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":null}]}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":{\"path\":\"/ok\",\"query\":null}}]}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":{\"path\":\"/ok\"},\"response\":null}]}")]
    [InlineData("{\"name\":\"bad\",\"protectedPaths\":[null]}")]
    [InlineData("{\"name\":\"bad\",\"rules\":[{\"id\":\"x\",\"match\":{\"path\":\"/ok\",\"qurey\":{\"account\":\"42\"}}}]}")]
    public async Task Invalid_nested_values_and_misspelled_conditions_return_400(string json)
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/admin/api/sessions", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Form_inspection_does_not_consume_proxied_body_and_malformed_json_is_not_proxied()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = builder.Build();
        var calls = 0;
        upstream.Run(async context => { Interlocked.Increment(ref calls); await context.Response.WriteAsync(await new StreamReader(context.Request.Body).ReadToEndAsync()); });
        await upstream.StartAsync();
        var url = upstream.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new object[] {
            new { id = "form", match = new { path = "/data", form = new { match = "yes" } }, response = new { text = "matched" } },
            new { id = "json", match = new { path = "/data", json = new { match = true } }, response = new { text = "matched" } }
        }, url);
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("no"), "match");
        var body = await multipart.ReadAsStringAsync();
        Assert.Equal(body, await (await client.PostAsync($"/s/{id}/data", multipart)).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/s/{id}/data", new StringContent("{bad", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Custom_methods_headers_delays_and_head_responses_follow_rules()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        var id = await SessionTests.Create(client, new[] { new { id = "any", match = new { path = "/custom" }, response = new { delayMs = 70, text = "hello", headers = new Dictionary<string, string> { ["X-Test"] = "mock" } } } });
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var custom = await client.SendAsync(new HttpRequestMessage(new HttpMethod("PROPFIND"), $"/s/{id}/custom"));
        Assert.Equal("hello", await custom.Content.ReadAsStringAsync());
        Assert.True(timer.ElapsedMilliseconds >= 60);
        Assert.Equal("mock", Assert.Single(custom.Headers.GetValues("X-Test")));
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"/s/{id}/custom"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Session_recursion_is_rejected_before_waiting_for_the_same_session_lock()
    {
        using var host = new TestHost();
        using var client = host.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var id = await SessionTests.Create(client, Array.Empty<object>());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var intermediary = builder.Build();
        intermediary.Run(async context =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/s/{id}/loop");
            request.Headers.TryAddWithoutValidation("X-Simple-Mock-Instance", context.Request.Headers["X-Simple-Mock-Instance"].ToArray());
            using var response = await client.SendAsync(request, context.RequestAborted);
            context.Response.StatusCode = (int)response.StatusCode;
            await context.Response.WriteAsync(await response.Content.ReadAsStringAsync(), context.RequestAborted);
        });
        await intermediary.StartAsync();
        var url = intermediary.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        await client.PutAsJsonAsync($"/admin/api/sessions/{id}", new { name = "loop", upstreamBaseUrl = url });
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync($"/s/{id}/loop")).StatusCode);
    }
}
