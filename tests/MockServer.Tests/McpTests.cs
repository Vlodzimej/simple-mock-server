using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static MockServer.Tests.TestResponses;

namespace MockServer.Tests;

public class McpTests
{
    [Fact]
    public async Task Agent_can_create_mock_verify_reset_and_import_using_real_mcp_client()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http);
        await using var mcp = await McpClient.CreateAsync(transport);
        var tools = await mcp.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "upsert_rule");
        Assert.Contains(tools, tool => tool.Name == "verify_requests");
        var result = await mcp.CallToolAsync("create_session", new Dictionary<string, object?> { ["configuration"] = new { name = "MCP task", rules = Array.Empty<object>() } });
        Assert.NotEqual(true, result.IsError);
        var created = Content(result);
        var id = (string)created["id"]!;
        var listed = await mcp.CallToolAsync("list_sessions");
        Assert.Equal(JsonValueKind.Object, listed.StructuredContent!.Value.ValueKind);
        Assert.Equal(id, (string?)Content(listed)["items"]![0]!["id"]);
        Assert.Equal($"/s/{id}", (string?)created["basePath"]);
        var rule = await mcp.CallToolAsync("upsert_rule", new Dictionary<string, object?> { ["sessionId"] = id, ["ruleId"] = "agent-rule", ["rule"] = SessionTests.Rule("/agent", "agent-rule", 201) });
        Assert.NotEqual(true, rule.IsError);
        Assert.Equal(HttpStatusCode.Created, (await http.PostAsync($"/s/{id}/agent", null)).StatusCode);
        var requests = await mcp.CallToolAsync("list_requests", new Dictionary<string, object?> { ["sessionId"] = id });
        Assert.Equal(JsonValueKind.Object, requests.StructuredContent!.Value.ValueKind);
        Assert.Equal("agent-rule", (string?)Content(requests)["items"]![0]!["ruleId"]);
        var verification = Content(await mcp.CallToolAsync("verify_requests", new Dictionary<string, object?> { ["sessionId"] = id, ["verification"] = new { ruleId = "agent-rule", count = 1 } }));
        Assert.True((bool)verification["matched"]!);
        var exported = Content(await mcp.CallToolAsync("export_session", new Dictionary<string, object?> { ["sessionId"] = id }));
        var imported = Content(await mcp.CallToolAsync("import_session", new Dictionary<string, object?> { ["configuration"] = JsonSerializer.Deserialize<JsonElement>(exported.ToJsonString()) }));
        Assert.NotEqual(id, (string?)imported["id"]);
        await mcp.CallToolAsync("set_scenario_state", new Dictionary<string, object?> { ["sessionId"] = id, ["state"] = "checked" });
        var current = Content(await mcp.CallToolAsync("get_session", new Dictionary<string, object?> { ["sessionId"] = id }));
        Assert.Equal("checked", (string?)current["state"]);
        current["name"] = "Updated by MCP";
        var update = await mcp.CallToolAsync("update_session", new Dictionary<string, object?> { ["sessionId"] = id, ["configuration"] = JsonSerializer.Deserialize<JsonElement>(current.ToJsonString()) });
        Assert.Equal("Updated by MCP", (string?)Content(update)["name"]);
        var invalid = await mcp.CallToolAsync("upsert_rule", new Dictionary<string, object?> { ["sessionId"] = id, ["ruleId"] = "invalid", ["rule"] = new { match = new { path = "relative" } } });
        Assert.True(invalid.IsError);
        Assert.Contains("путь", (string?)Content(invalid)["message"]);
        await mcp.CallToolAsync("reset_session", new Dictionary<string, object?> { ["sessionId"] = id });
        Assert.Empty((await Read(await http.GetAsync($"/admin/api/sessions/{id}/requests"))).AsArray());
        await mcp.CallToolAsync("delete_rule", new Dictionary<string, object?> { ["sessionId"] = id, ["ruleId"] = "agent-rule" });
        Assert.Equal(HttpStatusCode.BadGateway, (await http.PostAsync($"/s/{id}/agent", null)).StatusCode);
        await mcp.CallToolAsync("delete_session", new Dictionary<string, object?> { ["sessionId"] = id });
        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/s/{id}/agent", null)).StatusCode);
    }

    private static JsonNode Content(CallToolResult result) => JsonNode.Parse(string.Join("", result.Content.OfType<TextContentBlock>().Select(block => block.Text)))!;
}
