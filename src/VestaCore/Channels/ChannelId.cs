using System.Text.RegularExpressions;

namespace VestaCore.Channels;

/// <summary>
/// Validates and encapsulates a channel ID slug.
/// Format: [a-z0-9][a-z0-9\-/]*[a-z0-9], 1-128 characters.
/// </summary>
public static partial class ChannelId
{
    public const int MaxLength = 128;

    // Matches: starts with [a-z0-9], middle allows [a-z0-9\-/], ends with [a-z0-9]
    // Single-char IDs (e.g. "a") are also valid (start = end).
    [GeneratedRegex(@"^[a-z0-9]([a-z0-9\-/]*[a-z0-9])?$", RegexOptions.Compiled)]
    private static partial Regex Pattern();

    /// <summary>
    /// Validates a channel ID string. Returns true if the format is valid.
    /// </summary>
    public static bool IsValid(string? channelId)
    {
        if (string.IsNullOrEmpty(channelId))
            return false;

        if (channelId.Length > MaxLength)
            return false;

        // Disallow consecutive slashes or hyphens
        if (channelId.Contains("//") || channelId.Contains("--"))
            return false;

        return Pattern().IsMatch(channelId);
    }

    /// <summary>
    /// Validates a channel ID and throws if invalid.
    /// </summary>
    public static string Validate(string? channelId)
    {
        if (!IsValid(channelId))
            throw new ArgumentException(
                $"Invalid channel ID '{channelId}'. Must be 1-{MaxLength} chars, " +
                "lowercase alphanumeric with hyphens and slashes, no leading/trailing special chars.",
                nameof(channelId));

        return channelId!;
    }
}
