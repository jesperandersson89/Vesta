namespace VestaServer.Storage;

/// <summary>
/// In-process per-app message-count accountant for the current calendar-month usage
/// period, used to enforce <c>max_messages_per_month</c> without a per-PUBLISH SQL
/// aggregate. Durable values live in the <c>app_usage</c> table (see
/// <see cref="VestaServer.Data.Entities.AppUsageEntity"/>): the cache here is seeded
/// synchronously at startup (<c>AppUsageSeeder</c>) and refreshed by
/// <see cref="AppQuotaPrunerService"/>. Mirrors the cold-cache-allows policy of
/// <see cref="IAppStorageAccountant"/> for consistency: an unmeasured app is allowed
/// to publish until the next sweep populates it.
/// </summary>
public interface IAppUsageAccountant
{
  /// <summary>The current calendar-month period (UTC, first-of-month) the cached counts belong to.</summary>
  DateOnly CurrentPeriod { get; }

  /// <summary>
  /// Returns the cached message count for <paramref name="appId"/> in the current period,
  /// or <c>null</c> if unmeasured for this period (cold cache — callers should allow the PUBLISH).
  /// </summary>
  long? GetMessages(string appId);

  /// <summary>Replaces the cached message count for the current period (called by the pruner after a COUNT).</summary>
  void SetMessages(string appId, long count);

  /// <summary>Atomically increments the cached message count for the current period, seeding it to 1 if uncached.</summary>
  void IncrementMessages(string appId);
}

/// <summary>Default in-memory implementation. Singleton-scoped; resets its cache whenever the calendar month rolls over.</summary>
public sealed class InMemoryAppUsageAccountant(TimeProvider timeProvider) : IAppUsageAccountant
{
  private readonly object _lock = new();
  private readonly Dictionary<string, long> _messages = [];
  private DateOnly _period = PeriodOf(timeProvider.GetUtcNow());

  public DateOnly CurrentPeriod
  {
    get
    {
      lock (_lock)
      {
        RollOverIfNeeded();
        return _period;
      }
    }
  }

  public long? GetMessages(string appId)
  {
    lock (_lock)
    {
      RollOverIfNeeded();
      return _messages.TryGetValue(appId, out long value) ? value : null;
    }
  }

  public void SetMessages(string appId, long count)
  {
    lock (_lock)
    {
      RollOverIfNeeded();
      _messages[appId] = count;
    }
  }

  public void IncrementMessages(string appId)
  {
    lock (_lock)
    {
      RollOverIfNeeded();
      _messages[appId] = _messages.TryGetValue(appId, out long value) ? value + 1 : 1;
    }
  }

  private void RollOverIfNeeded()
  {
    DateOnly now = PeriodOf(timeProvider.GetUtcNow());
    if (now != _period)
    {
      _period = now;
      _messages.Clear();
    }
  }

  private static DateOnly PeriodOf(DateTimeOffset now) => new(now.UtcDateTime.Year, now.UtcDateTime.Month, 1);
}
