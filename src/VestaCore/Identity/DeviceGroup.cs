namespace VestaCore.Identity;

/// <summary>
/// The current materialized state of a device group — the set of public keys
/// (and their derived client IDs) that are trusted as members of the group.
///
/// <para>
/// Produced by <see cref="DeviceGroupProjection"/> by replaying the
/// <c>vesta/identity/{groupId}</c> channel. Membership is determined by the
/// rule: a device is in the group if it was vouched for (via a
/// <c>vesta.identity.link</c> event) by another device already in the group,
/// or if it is the founder (first <c>vesta.identity.announce</c> event on
/// the channel).
/// </para>
/// </summary>
/// <param name="GroupId">The 22-char base64url group identifier.</param>
/// <param name="Members">Map of client ID to the device's public key (base64url-encoded).</param>
public sealed record DeviceGroup(
    string GroupId,
    IReadOnlyDictionary<string, string> Members
)
{
    /// <summary>True if the given client ID is currently a trusted member of the group.</summary>
    public bool IsMember(string clientId) => Members.ContainsKey(clientId);

    /// <summary>The number of devices currently in the group.</summary>
    public int Count => Members.Count;
}

/// <summary>
/// Conventional channel ID for a device group's identity channel.
/// </summary>
public static class DeviceGroupChannel
{
    /// <summary>
    /// Returns the canonical channel ID for the given group: <c>vesta/identity/{groupId}</c>.
    /// </summary>
    public static string For(string groupId)
    {
        if (string.IsNullOrEmpty(groupId))
            throw new ArgumentException("Group ID must not be empty.", nameof(groupId));
        return $"vesta/identity/{groupId}";
    }
}
