namespace VestaClient.Relay;

/// <summary>A failed connection attempt against one relay candidate.</summary>
/// <param name="Relay">The relay that could not be reached.</param>
/// <param name="Reason">Short, human-readable failure reason (e.g. the socket error message).</param>
public sealed record RelayAttempt(Uri Relay, string Reason);

/// <summary>
/// Describes a relay outage that the client cannot heal on its own: every candidate (user override,
/// owner-manifest relays, compiled-in defaults) failed for <see cref="Passes"/> full passes.
/// </summary>
/// <param name="Attempts">The failures from the most recent pass, one per candidate.</param>
/// <param name="Passes">How many full passes over the candidate list failed.</param>
public sealed record RelaysExhaustedInfo(IReadOnlyList<RelayAttempt> Attempts, int Passes);

/// <summary>
/// Thrown by <c>ConnectAsync</c> when no candidate relay could be reached. Carries the per-relay
/// failure reasons so an app can hand them to a <see cref="RelayRecoverySession"/>.
/// </summary>
public sealed class RelaysExhaustedException(RelaysExhaustedInfo info)
    : InvalidOperationException($"Could not connect to any of the {info.Attempts.Count} configured relay(s).")
{
    public RelaysExhaustedInfo Info { get; } = info;
}
