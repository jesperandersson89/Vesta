using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VestaServer.Health;

/// <summary>Renders a <see cref="HealthReport"/> as the same lowercase-status JSON shape the old static endpoint used.</summary>
public static class HealthCheckResponseWriter
{
    public static Task Write(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        var payload = new
        {
            status = report.Status.ToString().ToLowerInvariant(),
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString().ToLowerInvariant(),
                description = entry.Value.Description,
            }),
        };
        return context.Response.WriteAsync(JsonSerializer.Serialize(payload));
    }
}
