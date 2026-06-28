using System.Text;
using System.Text.Json;
using VestaClient;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;

// ─── Configuration ───────────────────────────────────────────────────────────
// Relay URLs: VESTA_RELAY_URL (e.g. your Atrium-managed relay) > positional arg > local default.
// Supply a COMMA-SEPARATED list to enable failover — the client tries each in order and
// automatically switches to the next reachable relay if the active one goes away.
string relayConfig = Environment.GetEnvironmentVariable("VESTA_RELAY_URL")
    ?? (args.Length > 0 ? args[0] : "ws://localhost:5150/ws");

List<Uri> relays = relayConfig
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(u => new Uri(u))
    .ToList();
string serverUrl = relays[0].ToString();

// App namespace = the first channel segment. Set VESTA_APP_ID to the app id you
// provisioned in Atrium so every channel is scoped under it. Defaults to "chat".
string appId = Environment.GetEnvironmentVariable("VESTA_APP_ID") ?? "chat";
string channel = args.Length > 1 ? args[1] : $"{appId}/general";

// ─── Username Prompt ─────────────────────────────────────────────────────────
Console.Write("Enter your username: ");
string? username = Console.ReadLine()?.Trim();
if (string.IsNullOrWhiteSpace(username))
{
    Console.WriteLine("Username cannot be empty.");
    return;
}

string vestaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesta");
Directory.CreateDirectory(vestaDir);

// VESTA_IDENTITY_FILE lets you point at an identity you downloaded from Atrium
// (the app-owner key) instead of a per-username key generated locally.
string identityPath = Environment.GetEnvironmentVariable("VESTA_IDENTITY_FILE")
    ?? Path.Combine(vestaDir, $"chat-{username}-identity.json");
string dbPath = Path.Combine(vestaDir, $"chat-{username}.db");

// ─── Identity ────────────────────────────────────────────────────────────────
VestaIdentity identity = VestaIdentity.LoadOrCreate(identityPath);
string clientId = identity.ClientId;

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"Identity: {Path.GetFileName(identityPath)} ({clientId[..16]}...)");
Console.ResetColor();

// ─── Local Store (SQLite) ────────────────────────────────────────────────────
using SqliteClientEventStore localStore = new($"Data Source={dbPath}");

// ─── Banner ──────────────────────────────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║         Vesta Chat Room Example          ║");
Console.WriteLine("╠══════════════════════════════════════════╣");
Console.WriteLine($"║  User:     {username,-30}║");
Console.WriteLine($"║  Server:   {serverUrl[..25]}...{"",-2}║");
Console.WriteLine($"║  Channel:  {channel,-30}║");
Console.WriteLine($"║  Identity: {clientId[..16]}...{"",-11}║");
Console.WriteLine("╚══════════════════════════════════════════╝");
Console.WriteLine();

// Show how many local messages we already have cached
long lastSeq = await localStore.GetLatestSequenceAsync(channel);
if (lastSeq > 0)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"  [{lastSeq} messages cached locally — requesting catch-up from server]");
    Console.ResetColor();
}

Console.WriteLine("Connecting...");

// ─── Input State ─────────────────────────────────────────────────────────────
// We use a char-by-char input loop so incoming messages can be printed above
// the prompt without corrupting the typing area.
StringBuilder inputBuffer = new();
object consoleLock = new();
bool isConnected = false;

string GetPrompt() => isConnected ? $"[{username}] " : $"[offline] ";

void PrintAboveInput(Action printAction)
{
    lock (consoleLock)
    {
        // Clear the current input line
        string prompt = GetPrompt();
        int lineLen = prompt.Length + inputBuffer.Length;
        Console.Write('\r');
        Console.Write(new string(' ', lineLen));
        Console.Write('\r');

        // Print the message
        printAction();

        // Redraw the prompt + whatever the user has typed so far
        if (isConnected)
        {
            Console.ForegroundColor = ConsoleColor.Green;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
        }
        Console.Write(prompt);
        Console.ResetColor();
        Console.Write(inputBuffer);
    }
}

// ─── Connect ─────────────────────────────────────────────────────────────────
await using VestaConnection connection = new(clientId, localStore, identity)
{
    AutoReconnect = true
};

connection.OnEvent += (EventMessage evt) =>
{
    // Don't echo back our own messages
    if (evt.Event.ClientId == clientId)
        return;

    if (evt.Event.EventType == "app.chat.join")
    {
        string joiner = evt.Event.Payload.TryGetProperty("sender", out JsonElement s)
            ? s.GetString() ?? "someone"
            : "someone";
        string joinTime = evt.Event.Timestamp.ToLocalTime().ToString("HH:mm");
        PrintAboveInput(() =>
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"  {joinTime} * {joiner} joined the chat");
            Console.ResetColor();
        });
        return;
    }

    if (evt.Event.EventType == "app.chat.leave")
    {
        string leaver = evt.Event.Payload.TryGetProperty("sender", out JsonElement s)
            ? s.GetString() ?? "someone"
            : "someone";
        string leaveTime = evt.Event.Timestamp.ToLocalTime().ToString("HH:mm");
        PrintAboveInput(() =>
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  {leaveTime} * {leaver} left the chat");
            Console.ResetColor();
        });
        return;
    }

    PrintAboveInput(() => DisplayMessage(evt.Event, live: true));
};

connection.OnEventsBatch += (EventsBatchMessage batch) =>
{
    if (batch.Events.Count == 0)
        return;

    PrintAboveInput(() =>
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  ─── {batch.Events.Count} new message(s) from server ───");
        Console.ResetColor();

        foreach (SequencedEvent seq in batch.Events)
        {
            if (seq.Event.EventType is "app.chat.join" or "app.chat.leave")
            {
                string who = seq.Event.Payload.TryGetProperty("sender", out JsonElement s)
                    ? s.GetString() ?? "someone"
                    : "someone";
                string time = seq.Event.Timestamp.ToLocalTime().ToString("HH:mm");
                string action = seq.Event.EventType == "app.chat.join" ? "joined" : "left";
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {time} * {who} {action} the chat");
                Console.ResetColor();
            }
            else
            {
                DisplayMessage(seq.Event, live: false);
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─── end catch-up ───");
        Console.ResetColor();
    });
};

connection.OnAck += (AckMessage ack) =>
{
    // Silent — message confirmed persisted on server
};

connection.OnError += (ErrorMessage error) =>
{
    PrintAboveInput(() =>
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [ERROR] {error.Code}: {error.Message}");
        Console.ResetColor();
    });
};

connection.OnDisconnected += (string reason) =>
{
    isConnected = false;
    PrintAboveInput(() =>
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [DISCONNECTED] {reason}");
        Console.WriteLine("  Auto-reconnecting...");
        Console.ResetColor();
    });
};

connection.OnRelaySwitched += (Uri relay) =>
{
    PrintAboveInput(() =>
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  [RELAY] now using {relay}");
        Console.ResetColor();
    });
};

connection.OnReconnected += () =>
{
    isConnected = true;
    PrintAboveInput(() =>
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  [RECONNECTED] to {connection.ServerId}");
        Console.ResetColor();
    });
    _ = PublishJoinAsync();
};

async Task PublishJoinAsync()
{
    JsonElement joinPayload = JsonDocument.Parse(
        JsonSerializer.Serialize(new { sender = username })).RootElement;

    VestaEvent joinEvt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.chat.join",
        Payload: joinPayload);

    await connection.PublishAsync(joinEvt);
}

try
{
    await connection.ConnectAsync(
        relays,
        channels: [channel]);

    isConnected = true;
    await PublishJoinAsync();

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Connected to server: {connection.ServerId} via {connection.ActiveRelay}");
    Console.ResetColor();
    if (relays.Count > 1)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  Failover relays configured: {relays.Count} (will switch automatically if the active one drops)");
        Console.ResetColor();
    }
    Console.WriteLine("Type a message and press Enter. Press Ctrl+C to quit.\n");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Failed to connect: {ex.Message}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Running in offline mode — will try to reconnect on next message.\n");
    Console.ResetColor();
}

// ─── Message Loop (char-by-char) ─────────────────────────────────────────────
using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// Draw initial prompt
lock (consoleLock)
{
    if (isConnected)
    {
        Console.ForegroundColor = ConsoleColor.Green;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
    }
    Console.Write(GetPrompt());
    Console.ResetColor();
}

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        // Non-blocking check: wait for a key
        if (!Console.KeyAvailable)
        {
            await Task.Delay(50, cts.Token);
            continue;
        }

        ConsoleKeyInfo key = Console.ReadKey(intercept: true);

        if (key.Key == ConsoleKey.Enter)
        {
            string input = inputBuffer.ToString();
            inputBuffer.Clear();

            // Clear the current line (prompt + typed text) before printing formatted message
            string currentPrompt = GetPrompt();
            int lineLen = currentPrompt.Length + input.Length;
            Console.Write('\r');
            Console.Write(new string(' ', lineLen));
            Console.Write('\r');

            if (!string.IsNullOrWhiteSpace(input))
            {

                JsonElement payload = JsonDocument.Parse(
                    JsonSerializer.Serialize(new { text = input, sender = username })).RootElement;

                VestaEvent evt = new(
                    Id: Guid.NewGuid(),
                    ChannelId: channel,
                    Timestamp: DateTimeOffset.UtcNow,
                    ClientId: clientId,
                    EventType: "app.chat.message",
                    Payload: payload);

                await connection.PublishAsync(evt, cts.Token);

                // Display own message with timestamp
                string time = evt.Timestamp.ToLocalTime().ToString("HH:mm");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"  {time} [✓ {username}] ");
                Console.ResetColor();
                Console.WriteLine(input);

                if (!isConnected)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.WriteLine("  (queued to outbox)");
                    Console.ResetColor();
                }
            }

            // Redraw prompt
            lock (consoleLock)
            {
                if (isConnected)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                }
                Console.Write(GetPrompt());
                Console.ResetColor();
            }
        }
        else if (key.Key == ConsoleKey.Backspace)
        {
            if (inputBuffer.Length > 0)
            {
                lock (consoleLock)
                {
                    inputBuffer.Remove(inputBuffer.Length - 1, 1);
                    Console.Write("\b \b");
                }
            }
        }
        else if (key.KeyChar >= 32) // Printable character
        {
            lock (consoleLock)
            {
                inputBuffer.Append(key.KeyChar);
                Console.Write(key.KeyChar);
            }
        }
    }
}
catch (OperationCanceledException) { /* Ctrl+C */ }

Console.WriteLine("\nDisconnecting...");
if (isConnected)
{
    // Publish a leave event so others see we disconnected
    JsonElement leavePayload = JsonDocument.Parse(
        JsonSerializer.Serialize(new { sender = username })).RootElement;

    VestaEvent leaveEvt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.chat.leave",
        Payload: leavePayload);

    await connection.PublishAsync(leaveEvt);
    await Task.Delay(100); // Brief delay to let the message send before closing
    await connection.DisconnectAsync();
}
Console.WriteLine("Goodbye!");

// ─── Helper Functions ────────────────────────────────────────────────────────

static void DisplayMessage(VestaEvent evt, bool live)
{
    // Prefer the human-readable sender name from payload, fall back to truncated clientId
    string sender = evt.Payload.TryGetProperty("sender", out JsonElement senderEl)
        ? senderEl.GetString() ?? evt.ClientId[..8]
        : evt.ClientId[..8];

    string text = evt.Payload.TryGetProperty("text", out JsonElement textEl)
        ? textEl.GetString() ?? ""
        : evt.Payload.GetRawText();

    string signedIndicator = evt.Signature is not null ? "✓" : "?";
    string time = evt.Timestamp.ToLocalTime().ToString("HH:mm");

    if (live)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
    }

    Console.Write($"  {time} [{signedIndicator} {sender}] ");
    Console.ResetColor();
    Console.WriteLine(text);
}




