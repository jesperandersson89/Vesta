using System.Text.Json;
using Presence.CLI;
using VestaClient;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;

// ─── Configuration ───────────────────────────────────────────────────────────
const int HeartbeatIntervalSeconds = 5;
const int TtlSeconds = 15; // 3× heartbeat — user considered offline after this

string serverUrl = args.Length > 0 ? args[0] : "ws://localhost:5150/ws";
string appName = args.Length > 1 ? args[1] : "vesta-presence";
string channel = $"presence/{appName}";

// ─── Username ─────────────────────────────────────────────────────────────────
Console.Write("Enter your display name: ");
string? username = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(username))
{
    Console.WriteLine("Display name cannot be empty.");
    return;
}

// ─── Identity & Storage ──────────────────────────────────────────────────────
string vestaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesta");
Directory.CreateDirectory(vestaDir);

string identityPath = Path.Combine(vestaDir, $"presence-{appName}-{username}-identity.json");
string dbPath = Path.Combine(vestaDir, $"presence-{appName}-{username}.db");

VestaIdentity identity = VestaIdentity.LoadOrCreate(identityPath);
string clientId = identity.ClientId;

using SqliteClientEventStore localStore = new($"Data Source={dbPath}");

// ─── Presence State ──────────────────────────────────────────────────────────
PresenceState presence = new();

// ─── Display State (declared here so lambdas below can close over them) ──────
object _displayLock = new();

// ─── Connection ──────────────────────────────────────────────────────────────
bool isConnected = false;

await using VestaConnection connection = new(clientId, localStore, identity)
{
    AutoReconnect = true
};

connection.OnEvent += (EventMessage evt) =>
{
    presence.Apply(evt.Event);
    RedrawDisplay();
};

connection.OnEventsBatch += (EventsBatchMessage batch) =>
{
    foreach (SequencedEvent seq in batch.Events)
    {
        presence.Apply(seq.Event);
    }
    RedrawDisplay();
};

connection.OnDisconnected += (_) =>
{
    isConnected = false;
    RedrawDisplay();
};

connection.OnReconnected += () =>
{
    isConnected = true;
    _ = SendHeartbeatAsync();
    RedrawDisplay();
};

// ─── Connect ─────────────────────────────────────────────────────────────────
Console.Clear();

try
{
    await connection.ConnectAsync(new Uri(serverUrl), channels: [channel]);
    isConnected = true;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Could not connect to {serverUrl}: {ex.Message}");
    Console.WriteLine("Retrying automatically...");
    Console.ResetColor();
}

// Announce ourselves
if (isConnected)
{
    await SendHeartbeatAsync();
}

RedrawDisplay();

// ─── Background: Heartbeat Loop ──────────────────────────────────────────────
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

Task heartbeatLoop = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), cts.Token); }
        catch (OperationCanceledException) { break; }

        if (isConnected)
        {
            await SendHeartbeatAsync();
        }
    }
});

// ─── Background: Expiry Checker ──────────────────────────────────────────────
Task expiryLoop = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(1), cts.Token); }
        catch (OperationCanceledException) { break; }

        bool changed = presence.ExpireStaleUsers(DateTimeOffset.UtcNow);
        if (changed)
        {
            RedrawDisplay();
        }
    }
});

// ─── Wait for Ctrl+C ─────────────────────────────────────────────────────────
await Task.WhenAll(heartbeatLoop, expiryLoop);

// ─── Graceful Shutdown ───────────────────────────────────────────────────────
if (isConnected)
{
    JsonElement byePayload = JsonDocument.Parse(
        JsonSerializer.Serialize(new { username })).RootElement;

    VestaEvent byeEvt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.presence.bye",
        Payload: byePayload);

    await connection.PublishAsync(byeEvt);
    await Task.Delay(100); // Let the message send before closing
    await connection.DisconnectAsync();
}

Console.Clear();
Console.WriteLine($"\nGoodbye, {username}!");

// ─── Helpers ─────────────────────────────────────────────────────────────────

async Task SendHeartbeatAsync()
{
    JsonElement payload = JsonDocument.Parse(
        JsonSerializer.Serialize(new { username, status = "online" })).RootElement;

    JsonElement metadata = JsonDocument.Parse(
        JsonSerializer.Serialize(new { ttlSeconds = TtlSeconds })).RootElement;

    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.presence.heartbeat",
        Payload: payload,
        Replace: true,
        Metadata: metadata);

    // Apply our own heartbeat locally immediately (server won't echo it to us)
    presence.Apply(evt);

    await connection.PublishAsync(evt);
}

// ─── Display ─────────────────────────────────────────────────────────────────
void RedrawDisplay()
{
    lock (_displayLock)
    {
        Console.Clear();

        // Header
        string statusDot = isConnected ? "●" : "○";
        ConsoleColor dotColor = isConnected ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write("  ");
        Console.ForegroundColor = dotColor;
        Console.Write(statusDot);
        Console.ResetColor();
        Console.Write("  Who's Online");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        string statusLabel = isConnected ? "live" : "offline";
        Console.WriteLine($"  [{appName}] [{statusLabel}]");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {new string('─', 42)}");
        Console.ResetColor();

        IReadOnlyList<PresenceEntry> users = presence.AllUsers;

        if (users.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  (no one has been seen yet)");
            Console.ResetColor();
        }
        else
        {
            foreach (PresenceEntry user in users)
            {
                bool isSelf = user.ClientId == clientId;
                TimeSpan ago = DateTimeOffset.UtcNow - user.LastSeen;
                string seenLabel = ago.TotalSeconds < 2 ? "just now"
                    : ago.TotalSeconds < 60 ? $"{(int)ago.TotalSeconds}s ago"
                    : ago.TotalMinutes < 60 ? $"{(int)ago.TotalMinutes}m ago"
                    : $"{(int)ago.TotalHours}h ago";

                Console.Write("  ");

                if (user.Online)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("● ");
                    Console.ResetColor();

                    if (isSelf)
                    {
                        Console.ForegroundColor = ConsoleColor.White;
                        Console.Write($"{user.Username} ");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine("(you)");
                    }
                    else
                    {
                        Console.ForegroundColor = ConsoleColor.Cyan;
                        Console.Write($"{user.Username,-24}");
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"  {seenLabel}");
                    }
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("○ ");
                    Console.Write($"{user.Username,-24}");
                    Console.WriteLine($"  last seen {seenLabel}");
                }

                Console.ResetColor();
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  Heartbeat every {HeartbeatIntervalSeconds}s · TTL {TtlSeconds}s · Press Ctrl+C to quit");
        Console.ResetColor();
    }
}
