using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VestaClient;
using VestaClient.Relay;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using TodoList.CLI;

// ─── Configuration ───────────────────────────────────────────────────────────

// Flags: [--user <username>] [--password <password>] [serverUrl]
string? username = null;
string? password = null;
List<string> positional = [];

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--user" && i + 1 < args.Length)
        username = args[++i];
    else if (args[i] == "--password" && i + 1 < args.Length)
        password = args[++i];
    else
        positional.Add(args[i]);
}

string serverUrl = Environment.GetEnvironmentVariable("VESTA_RELAY_URL")
    ?? (positional.Count > 0 ? positional[0] : "ws://localhost:5150/ws");

// ─── Login Screen ───────────────────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("  ╭───────────────────────────────────────────────╮");
Console.WriteLine("  │         Vesta Todo List  —  Sign In            │");
Console.WriteLine("  ╰───────────────────────────────────────────────╯");
Console.ResetColor();
Console.WriteLine("  Any device that logs in with the same credentials");
Console.WriteLine("  will share the same list automatically.");
Console.WriteLine();

// Prompt interactively if not supplied on the command line.
if (string.IsNullOrWhiteSpace(username))
{
    Console.Write("  Username: ");
    username = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(username))
    {
        Console.Error.WriteLine("Username is required.");
        return;
    }
}
else
{
    Console.WriteLine($"  Username: {username}");
}

if (string.IsNullOrWhiteSpace(password))
{
    Console.Write("  Password: ");
    password = ReadPassword();
    if (string.IsNullOrWhiteSpace(password))
    {
        Console.Error.WriteLine("Password is required.");
        return;
    }
}
else
{
    Console.WriteLine("  Password: (provided)");
}
Console.WriteLine();

// Derive the channel from the credentials so any device with the same
// username + password automatically lands on the same list. The app namespace
// (first segment) comes from VESTA_APP_ID so you can scope it under the app id
// you provisioned in Atrium. Defaults to "todo".
string appId = Environment.GetEnvironmentVariable("VESTA_APP_ID") ?? "todo";
string channelSuffix = DeriveChannelSuffix(username, password);
string channel = $"{appId}/{channelSuffix}";
string listName = $"{username}'s list";

// ─── Identity & Storage ──────────────────────────────────────────────────────
string vestaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesta");
Directory.CreateDirectory(vestaDir);

// VESTA_IDENTITY_FILE lets you point at an identity downloaded from Atrium
// (the app-owner key) instead of the local default.
string identityPath = Environment.GetEnvironmentVariable("VESTA_IDENTITY_FILE")
    ?? Path.Combine(vestaDir, "todo-identity.json");
string dbPath = Path.Combine(vestaDir, $"todo-{channelSuffix}.db");

VestaIdentity identity = VestaIdentity.LoadOrCreate(identityPath);
string clientId = identity.ClientId;

// Every app declares a relay-independence trust anchor. VESTA_APP_OWNER_KEY (base64url)
// overrides it; with no env set we use this client's own public key. The relay comes from
// VESTA_RELAY_URL / the positional arg as the compiled-in default.
byte[] todoOwnerKey = Environment.GetEnvironmentVariable("VESTA_APP_OWNER_KEY") is { Length: > 0 } ownerEnv
    ? VestaCore.Utilities.Base64Url.Decode(ownerEnv.Trim())
    : identity.PublicKey;
VestaAppConfig appConfig = new(appId, todoOwnerKey, [new Uri(serverUrl)]);

using SqliteClientEventStore localStore = new($"Data Source={dbPath}");

// ─── Rebuild State from Local Cache ─────────────────────────────────────────
TodoListState state = new();

long lastSeq = await localStore.GetLatestSequenceAsync(channel);
if (lastSeq > 0)
{
    IReadOnlyList<SequencedEvent> cached = await localStore.GetEventsAsync(channel, fromSequence: 1, limit: int.MaxValue);
    state.Apply(cached);
}

// ─── Connection ──────────────────────────────────────────────────────────────
bool isConnected = false;
object displayLock = new();

await using VestaConnection connection = new(clientId, appConfig, localStore, identity)
{
    AutoReconnect = true
};

connection.OnEvent += (EventMessage evt) =>
{
    // Apply to projection
    SequencedEvent sequenced = new(evt.Event, evt.Sequence, DateTimeOffset.UtcNow);
    lock (displayLock)
    {
        state.Apply(sequenced);
    }

    // Show notification
    if (evt.Event.ClientId != clientId)
    {
        string who = evt.Event.Payload.TryGetProperty("createdBy", out JsonElement cb)
            ? cb.GetString() ?? "someone"
            : "someone";
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"\n  ↓ Synced: {DescribeEvent(evt.Event)} (from {who})");
        Console.ResetColor();
        PrintPrompt();
    }
};

connection.OnEventsBatch += (EventsBatchMessage batch) =>
{
    if (batch.Events.Count == 0)
        return;

    lock (displayLock)
    {
        state.Apply(batch.Events);
    }

    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"\n  ↓ Synced {batch.Events.Count} event(s) from server");
    Console.ResetColor();
    PrintTodos();
    PrintPrompt();
};

connection.OnDisconnected += (string reason) =>
{
    isConnected = false;
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n  [OFFLINE] {reason} — changes will sync when reconnected");
    Console.ResetColor();
    PrintPrompt();
};

connection.OnReconnected += () =>
{
    isConnected = true;
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("\n  [ONLINE] Reconnected — syncing...");
    Console.ResetColor();
    PrintPrompt();
};

// ─── Connect ─────────────────────────────────────────────────────────────────
try
{
    await connection.ConnectAsync(channels: [channel]);
    isConnected = true;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Could not connect: {ex.Message}");
    Console.WriteLine("Running in offline mode — changes will sync when server is available.");
    Console.ResetColor();
}


// ─── Banner ──────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.WriteLine("┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓");
Console.WriteLine("┃        Vesta Todo List                  ┃");
Console.WriteLine("┣━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┫");
Console.WriteLine($"┃  Signed in as  {username,-25}┃");
Console.WriteLine($"┃  Status        {(isConnected ? "✔ online" : "⚠ offline"),-25}┃");
Console.WriteLine("┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛");
Console.WriteLine();
PrintHelp();
PrintTodos();

// ─── Command Loop ────────────────────────────────────────────────────────────
while (true)
{
    PrintPrompt();
    string? input = Console.ReadLine()?.Trim();
    if (string.IsNullOrWhiteSpace(input))
        continue;

    string[] parts = input.Split(' ', 2, StringSplitOptions.TrimEntries);
    string command = parts[0].ToLowerInvariant();

    switch (command)
    {
        case "add" when parts.Length > 1:
            await AddItemAsync(parts[1]);
            break;

        case "done" when parts.Length > 1 && int.TryParse(parts[1], out int doneIdx):
            await ToggleItemAsync(doneIdx, done: true);
            break;

        case "undo" when parts.Length > 1 && int.TryParse(parts[1], out int undoIdx):
            await ToggleItemAsync(undoIdx, done: false);
            break;

        case "rename" when parts.Length > 1:
            await RenameItemAsync(parts[1]);
            break;

        case "remove" or "rm" when parts.Length > 1 && int.TryParse(parts[1], out int rmIdx):
            await RemoveItemAsync(rmIdx);
            break;

        case "list" or "ls":
            PrintTodos();
            break;

        case "help" or "?":
            PrintHelp();
            break;

        case "quit" or "exit" or "q":
            if (isConnected) await connection.DisconnectAsync();
            Console.WriteLine("Goodbye!");
            return;

        default:
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  Unknown command. Type 'help' for usage.");
            Console.ResetColor();
            break;
    }
}

// ─── Commands ────────────────────────────────────────────────────────────────

async Task AddItemAsync(string title)
{
    Guid itemId = Guid.NewGuid();

    JsonElement payload = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        id = itemId.ToString(),
        title,
        createdBy = clientId[..8]
    })).RootElement;

    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.todo.item-added",
        Payload: payload);

    // Optimistic local apply
    lock (displayLock) { state.ApplyLocal(evt); }

    await connection.PublishAsync(evt);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  ✓ Added: \"{title}\"");
    Console.ResetColor();
}

async Task ToggleItemAsync(int index, bool done)
{
    IReadOnlyList<TodoItem> items = state.Items;
    if (index < 1 || index > items.Count)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Invalid index. Use 1-{items.Count}.");
        Console.ResetColor();
        return;
    }

    TodoItem item = items[index - 1];

    JsonElement payload = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        id = item.Id.ToString(),
        done
    })).RootElement;

    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.todo.item-toggled",
        Payload: payload);

    lock (displayLock) { state.ApplyLocal(evt); }

    await connection.PublishAsync(evt);

    string action = done ? "completed" : "reopened";
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  ✓ Marked #{index} as {action}: \"{item.Title}\"");
    Console.ResetColor();
}

async Task RenameItemAsync(string args)
{
    // Expected format: "1 New title"
    string[] renameParts = args.Split(' ', 2, StringSplitOptions.TrimEntries);
    if (renameParts.Length < 2 || !int.TryParse(renameParts[0], out int idx))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  Usage: rename <index> <new title>");
        Console.ResetColor();
        return;
    }

    IReadOnlyList<TodoItem> items = state.Items;
    if (idx < 1 || idx > items.Count)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Invalid index. Use 1-{items.Count}.");
        Console.ResetColor();
        return;
    }

    TodoItem item = items[idx - 1];
    string newTitle = renameParts[1];

    JsonElement payload = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        id = item.Id.ToString(),
        title = newTitle
    })).RootElement;

    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.todo.item-renamed",
        Payload: payload);

    lock (displayLock) { state.ApplyLocal(evt); }

    await connection.PublishAsync(evt);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  ✓ Renamed #{idx}: \"{item.Title}\" → \"{newTitle}\"");
    Console.ResetColor();
}

async Task RemoveItemAsync(int index)
{
    IReadOnlyList<TodoItem> items = state.Items;
    if (index < 1 || index > items.Count)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Invalid index. Use 1-{items.Count}.");
        Console.ResetColor();
        return;
    }

    TodoItem item = items[index - 1];

    JsonElement payload = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        id = item.Id.ToString()
    })).RootElement;

    VestaEvent evt = new(
        Id: Guid.NewGuid(),
        ChannelId: channel,
        Timestamp: DateTimeOffset.UtcNow,
        ClientId: clientId,
        EventType: "app.todo.item-removed",
        Payload: payload);

    lock (displayLock) { state.ApplyLocal(evt); }

    await connection.PublishAsync(evt);

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"  ✓ Removed: \"{item.Title}\"");
    Console.ResetColor();
}

// ─── Credential & Display Helpers ────────────────────────────────────────────

/// <summary>
/// Derives a stable 16-character hex channel suffix from username + password
/// using SHA-256. Any device with the same credentials lands on the same channel.
/// </summary>
static string DeriveChannelSuffix(string user, string pass)
{
    byte[] input = Encoding.UTF8.GetBytes($"{user}:{pass}");
    byte[] hash  = SHA256.HashData(input);
    return Convert.ToHexString(hash)[..16].ToLowerInvariant();
}

/// <summary>Reads a password, echoing <c>*</c> for each character typed.</summary>
static string ReadPassword()
{
    StringBuilder sb = new();
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            break;
        }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0)
            {
                sb.Remove(sb.Length - 1, 1);
                Console.Write("\b \b"); // erase the last *
            }
        }
        else if (key.KeyChar >= ' ') // printable characters only
        {
            sb.Append(key.KeyChar);
            Console.Write('*');
        }
    }
    return sb.ToString();
}

void PrintTodos()
{
    IReadOnlyList<TodoItem> items = state.Items;

    if (items.Count == 0)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  (no items — use 'add <title>' to create one)");
        Console.ResetColor();
        return;
    }

    Console.WriteLine();
    for (int i = 0; i < items.Count; i++)
    {
        TodoItem item = items[i];
        string check = item.Done ? "✓" : " ";
        ConsoleColor color = item.Done ? ConsoleColor.DarkGray : ConsoleColor.White;

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {i + 1,2}. ");
        Console.ForegroundColor = item.Done ? ConsoleColor.Green : ConsoleColor.DarkGray;
        Console.Write($"[{check}] ");
        Console.ForegroundColor = color;
        Console.WriteLine(item.Title);
    }
    Console.ResetColor();
    Console.WriteLine();
}

void PrintHelp()
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine("  Commands:");
    Console.WriteLine("    add <title>          Add a new item");
    Console.WriteLine("    done <index>         Mark item as completed");
    Console.WriteLine("    undo <index>         Mark item as not completed");
    Console.WriteLine("    rename <index> <t>   Rename an item");
    Console.WriteLine("    remove <index>       Remove an item");
    Console.WriteLine("    list                 Show all items");
    Console.WriteLine("    help                 Show this help");
    Console.WriteLine("    quit                 Exit");
    Console.ResetColor();
    Console.WriteLine();
}

void PrintPrompt()
{
    Console.ForegroundColor = isConnected ? ConsoleColor.Green : ConsoleColor.Yellow;
    Console.Write(isConnected ? "todo> " : "todo[offline]> ");
    Console.ResetColor();
}


static string DescribeEvent(VestaEvent evt)
{
    return evt.EventType switch
    {
        "app.todo.item-added" => $"added \"{evt.Payload.GetProperty("title").GetString()}\"",
        "app.todo.item-toggled" => evt.Payload.GetProperty("done").GetBoolean()
            ? "completed an item"
            : "reopened an item",
        "app.todo.item-renamed" => $"renamed to \"{evt.Payload.GetProperty("title").GetString()}\"",
        "app.todo.item-removed" => "removed an item",
        _ => evt.EventType
    };
}
