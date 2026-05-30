using System.Text.Json;
using VestaClient;
using VestaCore.Events;
using VestaCore.Protocol;

// ─── Configuration ───────────────────────────────────────────────────────────
string serverUrl = args.Length > 0 ? args[0] : "ws://localhost:5150/ws";
string channel = args.Length > 1 ? args[1] : "chat/general";
string clientId = $"user-{Guid.NewGuid().ToString("N")[..8]}";

Console.WriteLine("╔══════════════════════════════════════╗");
Console.WriteLine("║       Vesta Chat Room Example        ║");
Console.WriteLine("╠══════════════════════════════════════╣");
Console.WriteLine($"║  Server:  {serverUrl,-26} ║");
Console.WriteLine($"║  Channel: {channel,-26} ║");
Console.WriteLine($"║  You are: {clientId,-26} ║");
Console.WriteLine("╚══════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine("Connecting...");

// ─── Connect ─────────────────────────────────────────────────────────────────
await using VestaConnection connection = new(clientId);

connection.OnEvent += (EventMessage evt) =>
{
    string sender = evt.Event.ClientId;
    string text = evt.Event.Payload.TryGetProperty("text", out JsonElement textEl)
        ? textEl.GetString() ?? ""
        : evt.Event.Payload.GetRawText();

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"  [{sender}] ");
    Console.ResetColor();
    Console.WriteLine(text);
};

connection.OnEventsBatch += (EventsBatchMessage batch) =>
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  --- {batch.Events.Count} messages from history ---");
    Console.ResetColor();

    foreach (SequencedEvent seq in batch.Events)
    {
        string sender = seq.Event.ClientId;
        string text = seq.Event.Payload.TryGetProperty("text", out JsonElement textEl)
            ? textEl.GetString() ?? ""
            : seq.Event.Payload.GetRawText();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  [{sender}] ");
        Console.ResetColor();
        Console.WriteLine(text);
    }

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  --- end history ---");
    Console.ResetColor();
};

connection.OnAck += (AckMessage ack) =>
{
    // Silent — just confirms our message was stored
};

connection.OnError += (ErrorMessage error) =>
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"  [ERROR] {error.Code}: {error.Message}");
    Console.ResetColor();
};

connection.OnDisconnected += (string reason) =>
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  [DISCONNECTED] {reason}");
    Console.ResetColor();
};

try
{
    await connection.ConnectAsync(
        new Uri(serverUrl),
        channels: [channel],
        lastSequences: new Dictionary<string, long> { [channel] = 0 }); // Get full history

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Connected to server: {connection.ServerId}");
    Console.ResetColor();
    Console.WriteLine("Type a message and press Enter. Press Ctrl+C to quit.\n");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Failed to connect: {ex.Message}");
    Console.ResetColor();
    return;
}

// ─── Message Loop ────────────────────────────────────────────────────────────
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"[{clientId}] ");
        Console.ResetColor();

        string? input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input))
            continue;

        JsonElement payload = JsonDocument.Parse(
            JsonSerializer.Serialize(new { text = input })).RootElement;

        VestaEvent evt = new(
            Id: Guid.NewGuid(),
            ChannelId: channel,
            Timestamp: DateTimeOffset.UtcNow,
            ClientId: clientId,
            EventType: "app.chat.message",
            Payload: payload);

        await connection.PublishAsync(evt, cts.Token);
    }
}
catch (OperationCanceledException) { /* Ctrl+C */ }

Console.WriteLine("\nDisconnecting...");
await connection.DisconnectAsync();
Console.WriteLine("Goodbye!");
