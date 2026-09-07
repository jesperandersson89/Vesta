using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace VestaServer.Health;

/// <summary>Verifies the Postgres connection backing the event store is actually reachable.</summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using NpgsqlCommand command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres connection failed", ex);
        }
    }
}
