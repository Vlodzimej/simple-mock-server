using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MockServer.Tests;

public sealed class TestHost : WebApplicationFactory<Program>
{
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"mock-{Guid.NewGuid()}.db");
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((context, config) =>
        config.AddInMemoryCollection(new Dictionary<string, string?> { ["DatabasePath"] = DatabasePath }));
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }) File.Delete(path);
    }
}

public static class TestResponses
{
    internal static async Task<JsonNode> Read(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
}
