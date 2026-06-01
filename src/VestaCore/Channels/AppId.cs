using System.Text.RegularExpressions;

namespace VestaCore.Channels;

/// <summary>
/// Validates an app ID and extracts the app namespace from a channel ID.
/// App IDs share the same character set as a single channel slug segment:
/// <c>[a-z0-9][a-z0-9\-]*[a-z0-9]</c>, 1-64 characters, no slashes (an app ID
/// is exactly the first segment of a channel ID).
/// </summary>
public static partial class AppId
{
  public const int MaxLength = 64;

  [GeneratedRegex(@"^[a-z0-9]([a-z0-9\-]*[a-z0-9])?$", RegexOptions.Compiled)]
  private static partial Regex Pattern();

  /// <summary>
  /// Returns true if the format is valid.
  /// </summary>
  public static bool IsValid(string? appId)
  {
    if (string.IsNullOrEmpty(appId)) return false;
    if (appId.Length > MaxLength) return false;
    if (appId.Contains("--")) return false;
    return Pattern().IsMatch(appId);
  }

  /// <summary>
  /// Validates and returns the app ID; throws if invalid.
  /// </summary>
  public static string Validate(string? appId)
  {
    if (!IsValid(appId))
      throw new ArgumentException(
          $"Invalid app ID '{appId}'. Must be 1-{MaxLength} chars, " +
          "lowercase alphanumeric with hyphens, no leading/trailing hyphens.",
          nameof(appId));
    return appId!;
  }

  /// <summary>
  /// Extracts the app namespace (first slug segment) from a channel ID.
  /// Returns <c>null</c> if the channel ID is empty or has no segment.
  /// Does not validate the channel ID format — caller should validate first.
  /// </summary>
  public static string? ExtractFromChannelId(string? channelId)
  {
    if (string.IsNullOrEmpty(channelId)) return null;
    int slash = channelId.IndexOf('/');
    return slash < 0 ? channelId : channelId[..slash];
  }
}
