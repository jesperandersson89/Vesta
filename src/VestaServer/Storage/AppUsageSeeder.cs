using Npgsql;

namespace VestaServer.Storage;

/// <summary>Synchronously seeds <see cref="IAppUsageAccountant"/> from the durable <c>app_usage</c> table at startup so the message-quota cache is never cold on a fresh boot (only a brand-new calendar period is cold, which is safe — zero usage is correct there).</summary>
public static class AppUsageSeeder
{
    public static async Task SeedAsync(
        NpgsqlDataSource dataSource,
        IAppUsageAccountant accountant,
        CancellationToken cancellationToken = default)
    {
        DateOnly periodStart = accountant.CurrentPeriod;
        const string sql = "SELECT app_id, messages FROM app_usage WHERE period_start = $1";
        await using NpgsqlCommand cmd = dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter<DateOnly> { TypedValue = periodStart });
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            accountant.SetMessages(reader.GetString(0), reader.GetInt64(1));
        }
    }
}
