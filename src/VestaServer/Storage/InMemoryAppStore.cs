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
      => Task.FromResult(_apps.ContainsKey(appId));

  public Task RegisterAsync(string appId, string ownerClientId, CancellationToken cancellationToken = default)
  {
    AppInfo info = new(appId, ownerClientId, DateTimeOffset.UtcNow, AppQuotas.None);
    if (!_apps.TryAdd(appId, info))
    {
      throw new AppAlreadyRegisteredException(appId);
    }
    return Task.CompletedTask;
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
      => Task.FromResult<IReadOnlyList<AppInfo>>([.. _apps.Values]);
}
