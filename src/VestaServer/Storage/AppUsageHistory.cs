using Npgsql;

namespace VestaServer.Storage;

/// <summary>One calendar-month message total for an app.</summary>
public sealed record AppUsagePeriod(DateOnly PeriodStart, long Messages);

/// <summary>Read-side access to the per-month message history kept in <c>app_usage</c>.</summary>
public interface IAppUsageHistory
{
  /// <summary>Most recent <paramref name="months"/> periods, oldest first. Periods with no row are omitted.</summary>
  Task<IReadOnlyList<AppUsagePeriod>> GetAsync(string appId, int months, CancellationToken cancellationToken = default);
}

/// <summary>In-memory mode keeps no history beyond the live counter for the current period.</summary>
public sealed class InMemoryAppUsageHistory(IAppUsageAccountant accountant) : IAppUsageHistory
{
  public Task<IReadOnlyList<AppUsagePeriod>> GetAsync(string appId, int months, CancellationToken cancellationToken = default)
  {
    long? messages = accountant.GetMessages(appId);
    IReadOnlyList<AppUsagePeriod> result = messages is long m ? [new AppUsagePeriod(accountant.CurrentPeriod, m)] : [];
    return Task.FromResult(result);
  }
}

public sealed class NpgsqlAppUsageHistory(NpgsqlDataSource dataSource) : IAppUsageHistory
{
  public async Task<IReadOnlyList<AppUsagePeriod>> GetAsync(string appId, int months, CancellationToken cancellationToken = default)
  {
    const string sql = """
            SELECT period_start, messages FROM (
              SELECT period_start, messages FROM app_usage
              WHERE app_id = $1
              ORDER BY period_start DESC
              LIMIT $2
            ) recent
            ORDER BY period_start ASC
            """;
    await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
    cmd.Parameters.Add(new NpgsqlParameter<string> { TypedValue = appId });
    cmd.Parameters.Add(new NpgsqlParameter<int> { TypedValue = months });
    List<AppUsagePeriod> rows = [];
    await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
    while (await reader.ReadAsync(cancellationToken))
      rows.Add(new AppUsagePeriod(reader.GetFieldValue<DateOnly>(0), reader.GetInt64(1)));
    return rows;
  }
}
