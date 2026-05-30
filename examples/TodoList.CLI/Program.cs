using System.Text.Json;
using VestaClient;
using VestaClient.Storage;
using VestaCore.Events;
using VestaCore.Identity;
using VestaCore.Protocol;
using TodoList.CLI;

// ─── Configuration ───────────────────────────────────────────────────────────
string serverUrl = args.Length > 0 ? args[0] : "ws://localhost:5150/ws";
string listName = args.Length > 1 ? args[1] : "my-todos";
string channel = $"todo/{listName}";

// ─── Identity & Storage ──────────────────────────────────────────────────────
string vestaDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vesta");
Directory.CreateDirectory(vestaDir);

string identityPath = Path.Combine(vestaDir, "todo-identity.json");
string dbPath = Path.Combine(vestaDir, $"todo-{listName}.db");

VestaIdentity identity = VestaIdentity.LoadOrCreate(identityPath);
string clientId = identity.ClientId;

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

await using VestaConnection connection = new(clientId, localStore, identity)
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
    await connection.ConnectAsync(new Uri(serverUrl), channels: [channel]);
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
Console.WriteLine("╔══════════════════════════════════════════╗");
Console.WriteLine("║        Vesta Todo List Example           ║");
Console.WriteLine("╠══════════════════════════════════════════╣");
Console.WriteLine($"║  List:     {listName,-30}║");
Console.WriteLine($"║  Channel:  {channel,-30}║");
Console.WriteLine($"║  Status:   {(isConnected ? "online" : "offline"),-30}║");
Console.WriteLine("╚══════════════════════════════════════════╝");
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

// ─── Display Helpers ─────────────────────────────────────────────────────────

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
