namespace VestaClient.Relay;

/// <summary>
/// Merges the possible sources of relay endpoints into a single ordered, de-duplicated
/// candidate list for <see cref="VestaConnection"/> to try in priority order.
///
/// Resolution precedence (highest first):
/// <list type="number">
///   <item>The user's local override — an individual escape hatch that always wins locally.</item>
///   <item>Relays from the latest owner-signed manifest — swarm-wide coordination.</item>
///   <item>The app's compiled-in defaults — first-run bootstrap and last-resort fallback.</item>
/// </list>
/// </summary>
public static class RelayResolver
{
    /// <summary>
    /// Produce the ordered relay candidate list. Duplicate URIs are removed, keeping the
    /// highest-priority occurrence.
    /// </summary>
    /// <param name="defaults">The app's compiled-in default relays. Must contain at least one entry.</param>
    /// <param name="userOverride">An optional user-chosen relay that takes precedence over everything else.</param>
    /// <param name="manifestRelays">Optional relays advertised by the latest verified owner-signed manifest.</param>
    public static IReadOnlyList<Uri> Resolve(
        IReadOnlyList<Uri> defaults,
        Uri? userOverride = null,
        IReadOnlyList<Uri>? manifestRelays = null)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        List<Uri> ordered = new();

        void Add(Uri? uri)
        {
            if (uri is null)
            {
                return;
            }
            if (!ordered.Contains(uri))
            {
                ordered.Add(uri);
            }
        }

        // 1. User local override always wins.
        Add(userOverride);

        // 2. Signed-manifest relays (swarm coordination).
        if (manifestRelays is not null)
        {
            foreach (Uri uri in manifestRelays)
            {
                Add(uri);
            }
        }

        // 3. App compiled-in defaults (bootstrap / last resort).
        foreach (Uri uri in defaults)
        {
            Add(uri);
        }

        if (ordered.Count == 0)
        {
            throw new ArgumentException("No relay candidates could be resolved — defaults were empty.", nameof(defaults));
        }

        return ordered;
    }
}
