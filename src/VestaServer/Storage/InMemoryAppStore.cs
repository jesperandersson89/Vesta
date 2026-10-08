using System.Collections.Concurrent;

namespace VestaServer.Storage;

/// <summary>
/// In-memory implementation of <see cref="IAppStore"/> for tests and the in-memory dev mode.
/// </summary>
public sealed class InMemoryAppStore : IAppStore
{
  private readonly ConcurrentDictionary<string, AppInfo> _apps = new();

  public Task<AppInfo?> GetAsync(string appId, CancellationToken cancellationToken = default)
  {
    _apps.TryGetValue(appId, out AppInfo? info);
    return Task.FromResult(info);
  }

  public Task<bool> ExistsAsync(string appId, CancellationToken cancellationToken = default)
      => Task.FromResult(_apps.TryGetValue(appId, out AppInfo? info) && info.DeletedAt is null);

  public Task RegisterAsync(string appId, string ownerClientId, bool discoverable = false, CancellationToken cancellationToken = default)
  {
    AppInfo info = new(appId, ownerClientId, DateTimeOffset.UtcNow, AppQuotas.None, discoverable);
    if (!_apps.TryAdd(appId, info))
    {
      throw new AppAlreadyRegisteredException(appId);
    }
    return Task.CompletedTask;
  }

  public Task<bool> SetDiscoverableAsync(string appId, bool discoverable, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing))
        return Task.FromResult(false);

      AppInfo updated = existing with { Discoverable = discoverable };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(true);
    }
  }

  public Task<bool> SetOwnerAsync(string appId, string ownerClientId, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing))
        return Task.FromResult(false);

      AppInfo updated = existing with { OwnerClientId = ownerClientId };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(true);
    }
  }

  public Task<bool> SetQuotasAsync(string appId, AppQuotas quotas, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing))
        return Task.FromResult(false);

      AppInfo updated = existing with { Quotas = quotas };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(true);
    }
  }

  public Task<IReadOnlyList<AppInfo>> ListAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<AppInfo>>([.. _apps.Values.Where(a => a.DeletedAt is null)]);

  public Task<IReadOnlyList<AppInfo>> ListAllAsync(CancellationToken cancellationToken = default)
      => Task.FromResult<IReadOnlyList<AppInfo>>([.. _apps.Values]);

  public Task<DateTimeOffset?> DeleteAsync(string appId, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing))
        return Task.FromResult<DateTimeOffset?>(null);
      if (existing.DeletedAt is not null)
        return Task.FromResult(existing.DeletedAt);

      AppInfo updated = existing with { DeletedAt = DateTimeOffset.UtcNow };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(updated.DeletedAt);
    }
  }

  public Task<bool> RestoreAsync(string appId, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing) || existing.DeletedAt is null)
        return Task.FromResult(false);

      AppInfo updated = existing with { DeletedAt = null };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(true);
    }
  }

  public Task<bool> SetPausedAsync(string appId, bool paused, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing))
        return Task.FromResult(false);

      AppInfo updated = existing with { PausedAt = paused ? existing.PausedAt ?? DateTimeOffset.UtcNow : null };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(true);
    }
  }

  public Task<bool> SetThrottleAsync(string appId, int? perMinute, CancellationToken cancellationToken = default)
  {
    while (true)
    {
      if (!_apps.TryGetValue(appId, out AppInfo? existing))
        return Task.FromResult(false);

      AppInfo updated = existing with { ThrottlePerMinute = perMinute };
      if (_apps.TryUpdate(appId, updated, existing))
        return Task.FromResult(true);
    }
  }
}
