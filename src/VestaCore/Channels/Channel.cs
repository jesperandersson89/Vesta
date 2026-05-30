using System.Text.Json;

namespace VestaCore.Channels;

/// <summary>
/// Channel metadata. Channels are identified by human-readable slugs.
/// </summary>
public sealed record Channel(
    string Id,                      // Validated slug (e.g. "myapp/todo-list")
    DateTimeOffset CreatedAt,       // When the channel was first created
    JsonElement? Metadata = null    // Optional app-specific configuration
);
