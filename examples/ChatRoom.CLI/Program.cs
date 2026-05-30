using System.Text;
using System.Text.Json;
using VestaClient;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;

// ─── Configuration ───────────────────────────────────────────────────────────
string serverUrl = args.Length > 0 ? args[0] : "ws://localhost:5150/ws";
string channel = args.Length > 1 ? args[1] : "chat/general";

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

string identityPath = Path.Combine(vestaDir, $"chat-{username}-identity.json");
string dbPath = Path.Combine(vestaDir, $"chat-{username}.db");

// ─── Identity ────────────────────────────────────────────────────────────────
VestaIdentity identity = LoadOrCreateIdentity(identityPath, username);
string clientId = identity.ClientId;

// ─── Local Store (SQLite) ────────────────────────────────────────────────────
using SqliteClientEventStore localStore = new($"Data Source={dbPath}");

// ─── Banner ──────────────────────────────────────────────────────────────────
Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║         Vesta Chat Room Example          ║");
Console.WriteLine("╠══════════════════════════════════════════╣");
Console.WriteLine($"║  User:     {username,-29}║");
Console.WriteLine($"║  Server:   {serverUrl,-29}║");
Console.WriteLine($"║  Channel:  {channel,-29}║");
Console.WriteLine($"║  Identity: {clientId[..16]}...{"",-12}║");
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
await using VestaConnection connection = new(clientId, localStore, identity);

connection.OnEvent += (EventMessage evt) =>
{
    // Don't echo back our own messages
    if (evt.Event.ClientId == clientId)
        return;

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
            DisplayMessage(seq.Event, live: false);
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
        Console.WriteLine("  Messages you type will be queued and sent on next connect.");
        Console.ResetColor();
    });
};

try
{
    // ConnectAsync uses localStore.GetChannelPositionsAsync() for smart catch-up
    await connection.ConnectAsync(
        new Uri(serverUrl),
        channels: [channel]);

    isConnected = true;

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Connected to server: {connection.ServerId}");
    Console.ResetColor();
    Console.WriteLine("Type a message and press Enter. Press Ctrl+C to quit.\n");
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Failed to connect: {ex.Message}");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine("Running in offline mode — messages will be queued for later.\n");
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

            // Move to next line
            Console.WriteLine();

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

    if (live)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
    }
    else
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
    }

    Console.Write($"  [{signedIndicator} {sender}] ");
    Console.ResetColor();
    Console.WriteLine(text);
}

static VestaIdentity LoadOrCreateIdentity(string path, string username)
{
    if (File.Exists(path))
    {
        string json = File.ReadAllText(path);
        JsonDocument doc = JsonDocument.Parse(json);
        string privateKeyBase64 = doc.RootElement.GetProperty("privateKey").GetString()!;
        byte[] privateKey = Base64UrlDecode(privateKeyBase64);

        VestaIdentity loaded = VestaIdentity.FromPrivateKey(privateKey);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"Loaded identity for '{username}' from {Path.GetFileName(path)}");
        Console.ResetColor();
        return loaded;
    }

    VestaIdentity generated = VestaIdentity.Generate();
    string publicKeyB64 = Base64UrlEncode(generated.PublicKey);
    string privateKeyB64 = Base64UrlEncode(generated.ExportPrivateKey());

    string identityJson = JsonSerializer.Serialize(new
    {
        username,
        publicKey = publicKeyB64,
        privateKey = privateKeyB64,
        clientId = generated.ClientId
    }, new JsonSerializerOptions { WriteIndented = true });

    File.WriteAllText(path, identityJson);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"Generated new identity for '{username}' → {Path.GetFileName(path)}");
    Console.ResetColor();
    return generated;
}

static string Base64UrlEncode(byte[] data)
{
    return Convert.ToBase64String(data)
        .Replace('+', '-')
        .Replace('/', '_')
        .TrimEnd('=');
}

static byte[] Base64UrlDecode(string base64Url)
{
    string base64 = base64Url
        .Replace('-', '+')
        .Replace('_', '/');

    int padding = (4 - base64.Length % 4) % 4;
    base64 += new string('=', padding);

    return Convert.FromBase64String(base64);
}
