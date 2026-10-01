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

// Flags: [--user <username>] [--password <password>] [--register] [serverUrl]
string? username = null;
string? password = null;
bool registerOnStart = false;
List<string> positional = [];

for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--user" && i + 1 < args.Length)
        username = args[++i];
    else if (args[i] == "--password" && i + 1 < args.Length)
        password = args[++i];
    else if (args[i] == "--register")
        registerOnStart = true;
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
string groupFilePath = Path.Combine(vestaDir, $"todo-{channelSuffix}-group.json");

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

connection.OnLimited += (VestaLimitNotice notice) =>
{
    Console.ForegroundColor = ConsoleColor.Red;
    string kind = notice.IsTransient ? "transient — will retry" : "permanent — not retried";
    Console.WriteLine($"\n  [LIMITED] {notice.Code}: {notice.Message} ({kind})");
    Console.ResetColor();
    PrintPrompt();
};

// ─── Connect ─────────────────────────────────────────────────────────────────
try
{
    await connection.ConnectAsync(channels: [channel]);
    isConnected = true;
    if (registerOnStart)
    {
        await connection.RegisterAppAsync(appId);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  Requested registration of app '{appId}' — watch for ACK/ERROR above.");
        Console.ResetColor();
    }
}
catch (RelaysExhaustedException ex)
{
    // The SDK opens its own relay picker (a local web page) when no relay is reachable.
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"Could not connect to any relay ({ex.Info.Attempts.Count} tried).");
    Console.WriteLine("Running in offline mode — changes will sync when server is available.");
    Console.ResetColor();
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

        case "/pair":
            await PairAsync();
            break;

        case "/join" when parts.Length > 1:
            await JoinAsync(parts[1]);
            break;

        case "/link" when parts.Length > 1:
            await LinkAsync(parts[1]);
            break;

        case "/devices":
            await ShowDevicesAsync();
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

// ─── Device pairing (cross-device identity) ─────────────────────────────────
// This is a SEPARATE mechanism from the username/password channel derivation above: the
// todo list itself already syncs across devices that share credentials. Device-group pairing
// instead proves that two distinct Ed25519 identities (two different local keypairs, e.g. one
// per device) belong to the same person via signed link events on a dedicated identity channel
// (`vesta/identity/{groupId}`) — see PLANNING.md "Cross-Device Identity (Device Groups)".

string? LoadGroupId()
    => File.Exists(groupFilePath)
        ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(groupFilePath)).GetProperty("groupId").GetString()
        : null;

void SaveGroupId(string groupId)
    => File.WriteAllText(groupFilePath, JsonSerializer.Serialize(new { groupId }));

async Task PairAsync()
{
    string? groupId = LoadGroupId();
    if (groupId is null)
    {
        groupId = await connection.CreateDeviceGroupAsync(deviceName: Environment.MachineName);
        SaveGroupId(groupId);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  Created device group '{groupId}' — this device is the founder.");
        Console.ResetColor();
    }

    PairingPayload payload = new(groupId, VestaCore.Utilities.Base64Url.Encode(identity.PublicKey), serverUrl);
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine("  Pairing code (paste into /join on the OTHER device):");
    Console.WriteLine($"    {payload.ToBase64()}");
    Console.ResetColor();
}

async Task JoinAsync(string pairingCode)
{
    PairingPayload payload;
    try
    {
        payload = PairingPayload.FromBase64(pairingCode);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  Invalid pairing code: {ex.Message}");
        Console.ResetColor();
        return;
    }

    SaveGroupId(payload.GroupId);
    await connection.JoinDeviceGroupAsync(payload.GroupId, deviceName: Environment.MachineName);

    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine($"  Announced this device to group '{payload.GroupId}'. Not yet trusted.");
    Console.WriteLine("  On an ALREADY-PAIRED device, run:");
    Console.WriteLine($"    /link {VestaCore.Utilities.Base64Url.Encode(identity.PublicKey)}");
    Console.ResetColor();
}

async Task LinkAsync(string targetPublicKeyBase64Url)
{
    string? groupId = LoadGroupId();
    if (groupId is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  This device isn't in a device group yet. Run /pair first.");
        Console.ResetColor();
        return;
    }

    byte[] targetPublicKey;
    try
    {
        targetPublicKey = VestaCore.Utilities.Base64Url.Decode(targetPublicKeyBase64Url);
    }
    catch (FormatException)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("  Invalid public key — expected the base64url string printed by /join.");
        Console.ResetColor();
        return;
    }

    await connection.LinkDeviceAsync(groupId, targetPublicKey, reason: "device-pairing");
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("  Vouched for the new device. It is now a trusted member of the group.");
    Console.ResetColor();
}

async Task ShowDevicesAsync()
{
    string? groupId = LoadGroupId();
    if (groupId is null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("  This device isn't in a device group yet. Run /pair first.");
        Console.ResetColor();
        return;
    }

    DeviceGroup group = await connection.GetDeviceGroupMembersAsync(groupId);
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine($"  Device group '{groupId}' — {group.Count} trusted member(s):");
    foreach ((string memberClientId, string memberPublicKey) in group.Members)
    {
        string marker = memberClientId == clientId ? " (this device)" : "";
        Console.WriteLine($"    {memberClientId}{marker}");
    }
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
    Console.WriteLine("    /pair                Create/show a device-group pairing code for THIS account");
    Console.WriteLine("    /join <code>         Join a device group using a pairing code from another device");
    Console.WriteLine("    /link <public-key>   Vouch for a device that ran /join (run on an already-paired device)");
    Console.WriteLine("    /devices             List devices trusted as members of this account's device group");
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
