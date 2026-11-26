using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using System.Text.Json;
using System.Text.Encodings.Web;
using Microsoft.Data.Sqlite;

namespace MockServer.Infrastructure;

public sealed class MockDbContext : DbContext
{
    public MockDbContext(DbContextOptions<MockDbContext> options) : base(options)
    {
        // Регистронезависимый поиск кириллицы вместо встроенного ASCII lower SQLite.
        ((SqliteConnection)Database.GetDbConnection()).CreateFunction<string?, string?>("lower", value => value?.ToLowerInvariant(), isDeterministic: true);
    }
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<SessionRequest> SessionRequests => Set<SessionRequest>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<SessionRequest>().Property(row => row.CreatedAt)
            .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
        model.Entity<SessionRequest>().HasIndex(row => new { row.SessionId, row.Id });
        model.Entity<SessionRequest>().HasOne<SessionRow>().WithMany().HasForeignKey(row => row.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class SessionRow
{
    public string Id { get; set; } = "";
    public string Payload { get; set; } = "";
}

public sealed class SessionRequest
{
    public long Id { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public string? RuleId { get; set; }
    public string Source { get; set; } = "";
    public int StatusCode { get; set; }
    public long DurationMs { get; set; }
    public string Reason { get; set; } = "";
    public string? QueryString { get; set; }
    [JsonIgnore]
    public string? DetailsJson { get; set; }
    [NotMapped, JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionRequestDetails? Details { get; set; }
}

public sealed class SessionRequestDetails
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
    public string RequestUrl { get; set; } = "";
    public string? UpstreamUrl { get; set; }
    public string Protocol { get; set; } = "";
    public string TraceId { get; set; } = "";
    public string? RemoteAddress { get; set; }
    public int RemotePort { get; set; }
    public string? LocalAddress { get; set; }
    public int LocalPort { get; set; }
    public string StateBefore { get; set; } = "";
    public string StateAfter { get; set; } = "";
    public DateTime CompletedAt { get; set; }
    public bool ClientAborted { get; set; }
    public Dictionary<string, string[]> RequestHeaders { get; set; } = [];
    public Dictionary<string, string[]> ResponseHeaders { get; set; } = [];
    public JournalBody? RequestBody { get; set; }
    public JournalBody? ResponseBody { get; set; }
}

public sealed record JournalBody(string Content, string Encoding, string? ContentType, long? Size, bool Truncated, string? Note = null);
