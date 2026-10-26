using System.Collections.Concurrent;

namespace AccountServer;

/// <summary>
/// Counts failed logins per key (a username, or a client address) in a fixed window and blocks a key that
/// reaches its limit until the window ends. In-memory and per process.
/// </summary>
/// <remarks>
/// Trade-off: a per-username limit lets anyone lock a known username out for the window. The login secret is
/// 256 bits, so online guessing is hopeless anyway; the limit mainly caps how fast someone can test password
/// guesses (each costs them a client-side Argon2id run). Raise the username limit or drop it if lockouts hurt.
/// </remarks>
public sealed class LoginThrottle(TimeProvider clock)
{
    public const int UserLimit = 10;
    public const int AddressLimit = 50;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset WindowEnds)> _failures = new();

    public bool IsBlocked(string key, int limit)
    {
        if (!_failures.TryGetValue(key, out (int Failures, DateTimeOffset WindowEnds) entry))
            return false;
        if (entry.WindowEnds <= clock.GetUtcNow())
        {
            _failures.TryRemove(key, out _);
            return false;
        }
        return entry.Failures >= limit;
    }

    public void RecordFailure(string key)
    {
        DateTimeOffset now = clock.GetUtcNow();
        _failures.AddOrUpdate(
            key,
            _ => (1, now + Window),
            (_, existing) => existing.WindowEnds <= now ? (1, now + Window) : (existing.Failures + 1, existing.WindowEnds));
    }

    public void Reset(string key) => _failures.TryRemove(key, out _);
}
