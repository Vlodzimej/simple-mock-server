using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ModelContextProtocol.Client;
using MockServer.Core;
using MockServer.Infrastructure;
using System.Text.Json;
using static MockServer.Tests.TestResponses;

namespace MockServer.Tests;

public class ProductTests
{
    [Fact]
    public async Task Database_upgrade_preserves_sessions_and_their_history()
    {
        using var host = new TestHost();
        var session = new MockSession { Id = "preserved", Name = "Existing task", State = "checked" };
        await using (var database = new MockDbContext(new DbContextOptionsBuilder<MockDbContext>().UseSqlite($"Data Source={host.DatabasePath}").Options))
        {
            await database.GetService<IMigrator>().MigrateAsync("20261124065746_UniversalSessions");
            database.Sessions.Add(new SessionRow { Id = session.Id, Payload = JsonSerializer.Serialize(session, SessionJson.Options) });
            await database.SaveChangesAsync();
            await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO SessionRequests (SessionId, CreatedAt, Method, Path, Source, StatusCode, DurationMs, Reason) VALUES ({session.Id}, {DateTime.UtcNow}, 'GET', '/example', 'mock', 200, 0, '')");
        }
        using var http = host.CreateClient();
        var restored = await Read(await http.GetAsync("/admin/api/sessions/preserved"));
        Assert.Equal("Existing task", (string?)restored["name"]);
        Assert.Equal("checked", (string?)restored["state"]);
        var history = (await Read(await http.GetAsync("/admin/api/sessions/preserved/requests"))).AsArray();
        Assert.Single(history);
        Assert.Equal("/example", (string?)history[0]!["path"]);
        var detail = await Read(await http.GetAsync($"/admin/api/sessions/preserved/requests/{history[0]!["id"]}"));
        Assert.Null(detail["details"]);
    }

    [Fact]
    public async Task Mcp_advertises_the_product_name()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp") }, http);
        await using var client = await McpClient.CreateAsync(transport);
        Assert.Equal("simple-mock-server", client.ServerInfo?.Name);
    }

    [Fact]
    public async Task New_database_contains_only_session_storage()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();
        Assert.Equal("[]", await (await http.GetAsync("/admin/api/sessions")).Content.ReadAsStringAsync());
        using var connection = new SqliteConnection($"Data Source={host.DatabasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name";
        using var reader = await command.ExecuteReaderAsync();
        var tables = new List<string>();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            if (!name.StartsWith("sqlite_") && !name.StartsWith("__EF")) tables.Add(name);
        }
        Assert.Equal(["SessionRequests", "Sessions"], tables);
    }
}
