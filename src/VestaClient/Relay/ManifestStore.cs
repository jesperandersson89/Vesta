using System.Text.Json;
using VestaCore.Relay;

namespace VestaClient.Relay;

/// <summary>
/// Caches the latest verified <see cref="RelayManifest"/> so a client can resolve relay
/// candidates from the most recent owner instruction even while offline, and can enforce the
/// monotonic-version (anti-rollback) rule across restarts.
/// </summary>
public interface IManifestStore
{
    /// <summary>The cached manifest, or null if none has been stored.</summary>
    RelayManifest? GetCached();

    /// <summary>Persist a manifest as the latest known-good one.</summary>
    void Save(RelayManifest manifest);
}

/// <summary>
/// A <see cref="IManifestStore"/> backed by a JSON file (typically next to the app's identity,
/// e.g. <c>~/.vesta/{prefix}-manifest.json</c>).
/// </summary>
public sealed class FileManifestStore(string filePath) : IManifestStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public RelayManifest? GetCached()
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<RelayManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(RelayManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(manifest, Options);
        File.WriteAllText(filePath, json);
    }
}
