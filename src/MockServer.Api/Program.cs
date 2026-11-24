using Microsoft.EntityFrameworkCore;
using MockServer.Api;
using MockServer.Core;
using MockServer.Infrastructure;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<MockDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var environment = services.GetRequiredService<IWebHostEnvironment>();
    var databasePath = Path.GetFullPath(configuration["DatabasePath"] ?? Path.Combine(environment.ContentRootPath, "data", "mock.db"));
    Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
    options.UseSqlite($"Data Source={databasePath}");
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 1048576);
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 1048576;
    options.ValueLengthLimit = 65536;
    options.ValueCountLimit = 100;
});
builder.Services.AddSingleton<MockInstance>();
builder.Services.AddSingleton<SessionLocks>();
builder.Services.AddScoped<SessionStore>();
builder.Services.AddScoped<SessionDispatcher>();
builder.Services.AddHttpForwarder();
builder.Services.AddSingleton<SessionProxy>();
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "simple-mock-server", Version = "1.0.0" })
    .WithHttpTransport().WithTools<SessionTools>(SessionJson.Options);
var origins = builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:4200"];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod()));
var app = builder.Build();
await using (var scope = app.Services.CreateAsyncScope())
    await scope.ServiceProvider.GetRequiredService<MockDbContext>().Database.MigrateAsync();
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSessionAdmin();
app.MapMcp("/mcp");
app.Map("/s/{sessionId}/{**path}", (HttpContext context, SessionDispatcher dispatcher) => dispatcher.Dispatch(context));
app.Map("/s", () => Results.NotFound(new { message = "Укажите сессию" }));
app.MapFallback("/admin/api/{**path}", () => Results.NotFound());
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
