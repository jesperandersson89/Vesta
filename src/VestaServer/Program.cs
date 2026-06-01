using System.Net.WebSockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using VestaCore.Storage;
using VestaServer.Connections;
using VestaServer.Data;
using VestaServer.Storage;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

int shutdownSeconds = builder.Configuration.GetValue<int>("ShutdownTimeoutSeconds", 3);
builder.Host.ConfigureHostOptions(options =>
{
    options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownSeconds);
});

// Register services
string? connectionString = builder.Configuration.GetConnectionString("Vesta");
bool useInMemory = builder.Configuration.GetValue<bool>("UseInMemoryStore", false);

if (useInMemory)
{
    // Explicit in-memory mode (for tests or local development without DB)
    builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
    builder.Services.AddSingleton<IChannelAccessStore, InMemoryChannelAccessStore>();
}
else if (!string.IsNullOrEmpty(connectionString))
{
    // PostgreSQL mode: EF Core for migrations, raw Npgsql for event hot path
    builder.Services.AddDbContext<VestaDbContext>(options =>
        options.UseNpgsql(connectionString));

    builder.Services.AddSingleton<NpgsqlDataSource>(_ =>
        NpgsqlDataSource.Create(connectionString));

    builder.Services.AddSingleton<IEventStore, NpgsqlEventStore>();
    builder.Services.AddSingleton<IChannelAccessStore, NpgsqlChannelAccessStore>();
}
else
{
    throw new InvalidOperationException(
        "No database configured. Set ConnectionStrings:Vesta to a PostgreSQL connection string, " +
        "or set UseInMemoryStore=true for development without a database.");
}

builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddTransient<ProtocolHandler>();

// Protocol options (e.g. require all events to be signed).
builder.Services.Configure<ProtocolOptions>(builder.Configuration.GetSection("Protocol"));

WebApplication app = builder.Build();

// Apply pending migrations on startup (only when using PostgreSQL)
if (!useInMemory && !string.IsNullOrEmpty(connectionString))
{
    using IServiceScope scope = app.Services.CreateScope();
    VestaDbContext dbContext = scope.ServiceProvider.GetRequiredService<VestaDbContext>();
    await dbContext.Database.MigrateAsync();
}

app.UseWebSockets();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.Map("/ws", async (HttpContext context, ProtocolHandler handler) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket connections only");
        return;
    }

    WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
    using ClientConnection connection = new(socket);

    await handler.HandleConnectionAsync(connection, context.RequestAborted);
});

app.Lifetime.ApplicationStopping.Register(() =>
{
    app.Logger.LogInformation($"Shutting down ({shutdownSeconds}s timeout)...");
});

app.Run();

// Make the implicit Program class accessible to WebApplicationFactory<Program> in tests
public partial class Program;
