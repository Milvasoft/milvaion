using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Cleans up old activity logs based on retention policy.
/// Prevents database bloat from accumulating audit logs.
/// Recommended schedule: Daily at 3 AM.
/// </summary>
public class ActivityLogCleanupJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.ActivityLogRetention;

        context.LogInformation("[ACTIVITY-LOG-CLEANUP] Activity log cleanup started");
        context.LogInformation($"Retention period: {settings.RetentionDays} days");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        try
        {
            var sql = @" DELETE FROM ""ActivityLogs"" WHERE ""ActivityDate"" < now() - interval '1 day' * @retentionDays";

            context.LogInformation($"Executing cleanup query...");

            var deletedCount = await connection.ExecuteAsync(sql, new { retentionDays = settings.RetentionDays });

            context.LogInformation($"[DONE] Activity log cleanup completed. Deleted records: {deletedCount}");

            return JsonSerializer.Serialize(new
            {
                Success = true,
                DeletedCount = deletedCount,
                settings.RetentionDays
            });
        }
        catch (Exception ex)
        {
            context.LogError($"[ERROR] Activity log cleanup failed: {ex.Message}");
            throw;
        }
    }
}

