using System.Text.RegularExpressions;

namespace VestaCore.Channels;

/// <summary>
/// Validates and encapsulates a channel ID slug.
/// Format: [a-z0-9][a-z0-9\-/]*[a-z0-9], 1-128 characters.
/// <para>
/// The <c>vesta/</c> prefix is reserved for protocol-level channels (e.g.
/// <c>vesta/identity/{groupId}</c> for device-group attestations). Apps must
/// not author channels in this namespace; see <see cref="IsProtocolChannel"/>.
/// </para>
/// </summary>
public static partial class ChannelId
{
    public const int MaxLength = 128;

    /// <summary>
    /// Reserved prefix for protocol-level channels. Apps may not create or
    /// publish to channels starting with this prefix; only Vesta-defined
    /// flows (identity linking, future protocol channels) are permitted.
    /// </summary>
    public const string ProtocolPrefix = "vesta/";

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
    /// Returns true if the channel ID is in the reserved <c>vesta/</c> protocol namespace.
    /// </summary>
    public static bool IsProtocolChannel(string? channelId)
    {
        return channelId is not null && channelId.StartsWith(ProtocolPrefix, StringComparison.Ordinal);
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

    /// <summary>
    /// Validates a channel ID for an app-authored write (PUBLISH, CREATE_CHANNEL).
    /// Throws if the ID is invalid OR if it targets the reserved <c>vesta/</c> namespace.
    /// Use <see cref="Validate"/> for reads (SUBSCRIBE, FETCH) where the protocol namespace is allowed.
    /// </summary>
    public static string ValidateForAppWrite(string? channelId)
    {
        string validated = Validate(channelId);
        if (IsProtocolChannel(validated))
        {
            throw new ArgumentException(
                $"Channel ID '{channelId}' uses the reserved '{ProtocolPrefix}' prefix. " +
                "This namespace is reserved for protocol-level channels and may not be authored by apps.",
                nameof(channelId));
        }
        return validated;
    }
}
