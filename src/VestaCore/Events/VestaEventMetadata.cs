using System.Text.Json;

namespace VestaCore.Events;

/// <summary>
/// Helpers for reading well-known fields out of <see cref="VestaEvent.Metadata"/>.
/// Metadata is an open JSON object so apps can carry their own transport hints;
/// these helpers centralize the conventions that the Vesta server understands.
/// </summary>
public static class VestaEventMetadata
{
  /// <summary>
  /// Standard key for the time-to-live hint, in seconds.
  /// When present and positive, the server computes <c>expires_at = received_at + ttlSeconds</c>
  /// and filters the event out of catch-up reads after that moment.
  /// </summary>
  public const string TtlSecondsKey = "ttlSeconds";

  /// <summary>
  /// Try to read <see cref="TtlSecondsKey"/> from the event's metadata.
  /// Returns false (with <paramref name="ttlSeconds"/> set to 0) when metadata is
  /// missing, the key is absent, or the value is not a positive integer.
  /// </summary>
  public static bool TryGetTtlSeconds(VestaEvent evt, out int ttlSeconds)
  {
    ttlSeconds = 0;
    if (evt.Metadata is not JsonElement metadata || metadata.ValueKind != JsonValueKind.Object)
    {
      return false;
    }

    if (!metadata.TryGetProperty(TtlSecondsKey, out JsonElement ttlElement))
    {
      return false;
    }

    if (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt32(out int value) || value <= 0)
    {
      return false;
    }

    ttlSeconds = value;
    return true;
  }
}
