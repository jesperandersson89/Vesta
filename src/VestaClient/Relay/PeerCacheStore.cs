using System.Text.Json;
using VestaClient.Federation;

namespace VestaClient.Relay;

/// <summary>
/// Remembers relays learned from federation while the app was healthy, so recovery can offer
/// choices even when every relay the app knows about is dead. Entries are hints only — they are
/// never auto-adopted and are not re-verified against descriptor TTLs.
/// </summary>
public interface IPeerCacheStore
{
    IReadOnlyList<DiscoveredRelay> Load();

    void Save(IReadOnlyList<DiscoveredRelay> peers);
}

public sealed class InMemoryPeerCacheStore : IPeerCacheStore
{
    private IReadOnlyList<DiscoveredRelay> _peers = [];

    public IReadOnlyList<DiscoveredRelay> Load() => _peers;

    public void Save(IReadOnlyList<DiscoveredRelay> peers) => _peers = [.. peers];
}

/// <summary>A <see cref="IPeerCacheStore"/> backed by a JSON file (<c>~/.vesta/relays/{appId}.peers.json</c>).</summary>
public sealed class FilePeerCacheStore(string filePath) : IPeerCacheStore
{
    public const int MaxEntries = 64;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record Entry(string RelayPublicKey, List<string> Urls, bool HostsRequestedApp, DateTimeOffset IssuedAt);

    public IReadOnlyList<DiscoveredRelay> Load()
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            List<Entry>? entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(filePath), Options);
            List<DiscoveredRelay> peers = [];
            foreach (Entry entry in entries ?? [])
            {
                List<Uri> urls = [];
                foreach (string url in entry.Urls ?? [])
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                    {
                        urls.Add(uri);
                    }
                }
                if (urls.Count > 0 && !string.IsNullOrEmpty(entry.RelayPublicKey))
                {
                    peers.Add(new DiscoveredRelay(entry.RelayPublicKey, urls, entry.HostsRequestedApp, entry.IssuedAt));
                }
            }
            return peers;
        }
        catch (JsonException)
        {
            // A corrupt cache is treated as empty rather than failing the app.
            return [];
        }
    }

    public void Save(IReadOnlyList<DiscoveredRelay> peers)
    {
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<Entry> entries = [.. peers
            .Take(MaxEntries)
            .Select(p => new Entry(p.RelayPublicKey, [.. p.Urls.Select(u => u.ToString())], p.HostsRequestedApp, p.IssuedAt))];
        File.WriteAllText(filePath, JsonSerializer.Serialize(entries, Options));
    }
}
