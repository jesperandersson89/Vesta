using System.Collections.Concurrent;

namespace VestaServer.Connections;

/// <summary>
/// Per-(app, client) publish rate limiter. Implements a simple per-minute token bucket:
/// each (appId, clientId) pair gets up to <c>publishRatePerMinute</c> events per rolling
/// 60-second window. Buckets refill continuously at <c>rate / 60</c> tokens per second
/// and are capped at the configured rate (no burst beyond the per-minute allowance).
///
/// In-memory and per-process — this is good enough for the relay's current single-host
/// deployment. A multi-host deployment (TODO #15) would need a shared backend.
/// </summary>
public sealed class AppRateLimiter
{
  private readonly ConcurrentDictionary<(string AppId, string ClientId), Bucket> _buckets = new();
  private readonly Func<DateTimeOffset> _clock;

  public AppRateLimiter() : this(() => DateTimeOffset.UtcNow) { }

  /// <summary>Test seam — inject a deterministic clock.</summary>
  public AppRateLimiter(Func<DateTimeOffset> clock)
  {
    _clock = clock;
  }

  /// <summary>
  /// Try to consume one token for <paramref name="clientId"/> publishing under
  /// <paramref name="appId"/>. Returns true if allowed, false if the rate limit would be exceeded.
  /// </summary>
  public bool TryAcquire(string appId, string clientId, int ratePerMinute)
  {
    if (ratePerMinute <= 0)
      return true; // no limit configured

    Bucket bucket = _buckets.GetOrAdd((appId, clientId), _ => new Bucket(ratePerMinute, _clock()));
    return bucket.TryAcquire(ratePerMinute, _clock());
  }

  private sealed class Bucket(double initialTokens, DateTimeOffset now)
  {
    private double _tokens = initialTokens;
    private DateTimeOffset _lastRefill = now;
    private readonly Lock _lock = new();

    public bool TryAcquire(int ratePerMinute, DateTimeOffset now)
    {
      lock (_lock)
      {
        double elapsedSeconds = (now - _lastRefill).TotalSeconds;
        if (elapsedSeconds > 0)
        {
          _tokens = Math.Min(ratePerMinute, _tokens + elapsedSeconds * (ratePerMinute / 60.0));
          _lastRefill = now;
        }

        if (_tokens >= 1.0)
        {
          _tokens -= 1.0;
          return true;
        }
        return false;
      }
    }
  }
}
