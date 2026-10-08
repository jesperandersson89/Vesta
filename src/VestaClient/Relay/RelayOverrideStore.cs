using System.Text.Json;

namespace Vesta;

/// <summary>
/// The user's chosen relay plus the per-relay inputs the app needs to live there.
/// </summary>
/// <param name="Relay">The relay to use.</param>
/// <param name="AppId">The app namespace to use on that relay, or null to keep the app's own id.</param>
/// <param name="RegisterApp">True to send REGISTER_APP for <see cref="AppId"/> after connecting.</param>
public sealed record RelayOverride(Uri Relay, string? AppId = null, bool RegisterApp = false);

/// <summary>
/// Persists the user's local relay override — the individual escape hatch that lets an
/// end-user point an app at a different relay regardless of what the app ships with or
/// what the owner's manifest advertises. The override always wins locally.
/// </summary>
public interface IRelayOverrideStore
{
    /// <summary>The currently-stored override, or null if none is set.</summary>
    RelayOverride? GetOverride();

    /// <summary>Persist a relay override.</summary>
    void SetOverride(RelayOverride relayOverride);

    /// <summary>Remove any stored override, reverting to manifest/default resolution.</summary>
    void ClearOverride();
}

/// <summary>
/// A <see cref="IRelayOverrideStore"/> backed by a small JSON file (typically stored next
/// to the app's identity file, e.g. <c>~/.vesta/{prefix}-relays.json</c>).
/// File format: <c>{ "relay": "wss://...", "appId": "...", "registerApp": false }</c>; only
/// <c>relay</c> is required, so files written by older versions still load.
/// </summary>
public sealed class FileRelayOverrideStore(string filePath) : IRelayOverrideStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public RelayOverride? GetOverride()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("relay", out JsonElement relayElement) ||
                relayElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = relayElement.GetString();
            if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            {
                return null;
            }

            string? appId = doc.RootElement.TryGetProperty("appId", out JsonElement appIdElement) &&
                            appIdElement.ValueKind == JsonValueKind.String
                ? appIdElement.GetString()
                : null;
            bool registerApp = doc.RootElement.TryGetProperty("registerApp", out JsonElement registerElement) &&
                               registerElement.ValueKind == JsonValueKind.True;

            return new RelayOverride(uri, string.IsNullOrWhiteSpace(appId) ? null : appId, registerApp);
        }
        catch (JsonException)
        {
            // A corrupt override file is treated as "no override" rather than failing the app.
        }

        return null;
    }

    public void SetOverride(RelayOverride relayOverride)
    {
        ArgumentNullException.ThrowIfNull(relayOverride);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(
            new
            {
                relay = relayOverride.Relay.ToString(),
                appId = relayOverride.AppId,
                registerApp = relayOverride.RegisterApp,
            },
            WriteOptions);
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
