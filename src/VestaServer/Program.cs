using System.Net.WebSockets;
using VestaCore.Storage;
using VestaServer.Connections;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddSingleton<IEventStore, InMemoryEventStore>();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddTransient<ProtocolHandler>();

WebApplication app = builder.Build();

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

app.Run();

// Make the implicit Program class accessible to WebApplicationFactory<Program> in tests
public partial class Program;
