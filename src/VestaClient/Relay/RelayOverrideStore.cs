using System.Text.Json;

namespace VestaClient.Relay;

/// <summary>
/// Persists the user's local relay override — the individual escape hatch that lets an
/// end-user point an app at a different relay regardless of what the app ships with or
/// what the owner's manifest advertises. The override always wins locally.
/// </summary>
public interface IRelayOverrideStore
{
    /// <summary>The currently-stored override, or null if none is set.</summary>
    Uri? GetOverride();

    /// <summary>Persist a relay override.</summary>
    void SetOverride(Uri relay);

    /// <summary>Remove any stored override, reverting to manifest/default resolution.</summary>
    void ClearOverride();
}

/// <summary>
/// A <see cref="IRelayOverrideStore"/> backed by a small JSON file (typically stored next
/// to the app's identity file, e.g. <c>~/.vesta/{prefix}-relays.json</c>).
/// File format: <c>{ "relay": "wss://..." }</c>.
/// </summary>
public sealed class FileRelayOverrideStore(string filePath) : IRelayOverrideStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public Uri? GetOverride()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("relay", out JsonElement relayElement) &&
                relayElement.ValueKind == JsonValueKind.String)
            {
                string? value = relayElement.GetString();
                if (!string.IsNullOrWhiteSpace(value) &&
                    Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                {
                    return uri;
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt override file is treated as "no override" rather than failing the app.
        }

        return null;
    }

    public void SetOverride(Uri relay)
    {
        ArgumentNullException.ThrowIfNull(relay);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(new { relay = relay.ToString() }, WriteOptions);
        File.WriteAllText(filePath, json);
    }

    public void ClearOverride()
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
}
