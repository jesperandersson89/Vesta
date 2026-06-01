using VestaServer.Connections;

namespace VestaServer.Tests;

public class AppRateLimiterTests
{
  [Fact]
  public void NoLimit_AlwaysAllows()
  {
    AppRateLimiter limiter = new();
    for (int i = 0; i < 1000; i++)
      Assert.True(limiter.TryAcquire("app", "client", ratePerMinute: 0));
  }

  [Fact]
  public void EnforcesBurstUpToRate()
  {
    DateTimeOffset now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    AppRateLimiter limiter = new(() => now);

    for (int i = 0; i < 10; i++)
      Assert.True(limiter.TryAcquire("app", "client", ratePerMinute: 10));

    Assert.False(limiter.TryAcquire("app", "client", ratePerMinute: 10));
  }

  [Fact]
  public void RefillsOverTime()
  {
    DateTimeOffset now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    AppRateLimiter limiter = new(() => now);

    // Burn the initial 10 tokens.
    for (int i = 0; i < 10; i++)
      limiter.TryAcquire("app", "client", ratePerMinute: 10);
    Assert.False(limiter.TryAcquire("app", "client", ratePerMinute: 10));

    // Advance 30s — at 10/min that refills 5 tokens.
    now = now.AddSeconds(30);
    for (int i = 0; i < 5; i++)
      Assert.True(limiter.TryAcquire("app", "client", ratePerMinute: 10));
    Assert.False(limiter.TryAcquire("app", "client", ratePerMinute: 10));
  }

  [Fact]
  public void BucketsAreIsolatedPerAppAndClient()
  {
    DateTimeOffset now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    AppRateLimiter limiter = new(() => now);

    for (int i = 0; i < 5; i++)
      Assert.True(limiter.TryAcquire("app-a", "client-1", ratePerMinute: 5));
    Assert.False(limiter.TryAcquire("app-a", "client-1", ratePerMinute: 5));

    // Different client — fresh bucket.
    Assert.True(limiter.TryAcquire("app-a", "client-2", ratePerMinute: 5));
    // Different app — fresh bucket.
    Assert.True(limiter.TryAcquire("app-b", "client-1", ratePerMinute: 5));
  }
}
