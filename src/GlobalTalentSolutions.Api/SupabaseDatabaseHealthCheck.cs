using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

public sealed class SupabaseDatabaseHealthCheck(IServiceProvider services) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var database = services.GetService<NpgsqlDataSource>();
        if (database is null)
            return HealthCheckResult.Unhealthy("Supabase database connection is not configured.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await using var command = database.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Connection details and provider errors can contain secrets; expose neither.
            return HealthCheckResult.Unhealthy("Supabase database connection failed.");
        }
    }
}
