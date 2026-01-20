using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Cleans up old internal notifications based on retention policy.
/// Removes seen and unseen notifications after specified retention periods.
/// Recommended schedule: Daily at 4 AM.
/// </summary>
public class NotificationCleanupJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.NotificationRetention;

        context.LogInformation("[NOTIFICATION-CLEANUP] Notification cleanup started");
        context.LogInformation($"Retention: Seen={settings.SeenRetentionDays}d, Unseen={settings.UnseenRetentionDays}d");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        try
        {
            var sql = @" DELETE FROM ""InternalNotifications"" WHERE ""SeenDate"" < now() - interval '1 day' * @seenRetentionDays
OR (""SeenDate"" IS NULL AND ""CreatedDate"" < now() - interval '1 day' * @unseenRetentionDays)";

            context.LogInformation($"Executing cleanup query...");

            var deletedCount = await connection.ExecuteAsync(sql, new
            {
                seenRetentionDays = settings.SeenRetentionDays,
                unseenRetentionDays = settings.UnseenRetentionDays
            });

            context.LogInformation($"[DONE] Notification cleanup completed. Deleted records: {deletedCount}");

            return JsonSerializer.Serialize(new
            {
                Success = true,
                DeletedCount = deletedCount,
                settings.SeenRetentionDays,
                settings.UnseenRetentionDays
            });
        }
        catch (Exception ex)
        {
            context.LogError($"[ERROR] Notification cleanup failed: {ex.Message}");
            throw;
        }
    }
}

