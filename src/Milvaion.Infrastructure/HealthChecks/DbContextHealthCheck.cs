using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Milvaion.Infrastructure.Persistence.Context;

namespace Milvaion.Infrastructure.HealthChecks;

/// <summary>
/// Health check for PostgreSQL database connection via DbContext.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DbContextHealthCheck"/> class.
/// </remarks>
public class DbContextHealthCheck(MilvaionDbContext dbContext) : IHealthCheck
{
    private readonly MilvaionDbContext _dbContext = dbContext;

    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Try to execute a simple query to verify connection
            var canConnect = await _dbContext.Database.CanConnectAsync(cancellationToken);

            if (!canConnect)
            {
                return HealthCheckResult.Unhealthy("Cannot connect to PostgreSQL database", null, new Dictionary<string, object>
                {
                    ["DatabaseName"] = _dbContext.Database.GetDbConnection().Database,
                    ["ConnectionStatus"] = "Disconnected"
                });
            }

            // Execute a simple query to verify database is responsive
            await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);

            return HealthCheckResult.Healthy("PostgreSQL database connection is healthy", new Dictionary<string, object>
            {
                ["DatabaseName"] = _dbContext.Database.GetDbConnection().Database,
                ["ConnectionStatus"] = "Connected",
                ["ProviderName"] = _dbContext.Database.ProviderName
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL database health check failed", ex, new Dictionary<string, object>
            {
                ["Error"] = ex.Message,
                ["ExceptionType"] = ex.GetType().Name
            });
        }
    }
}
