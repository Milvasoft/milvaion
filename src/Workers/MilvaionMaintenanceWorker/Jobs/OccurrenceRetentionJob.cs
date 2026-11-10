using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Cleans up old job occurrences based on retention policy.
/// Prevents database bloat from accumulating historical data.
/// Recommended schedule: Daily at 2 AM.
/// </summary>
public class OccurrenceRetentionJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult<string>
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.OccurrenceRetention;
        var results = new Dictionary<string, (int occurrences, int logs)>();
        var totalDeleted = 0;
        var totalLogsDeleted = 0;

        context.LogInformation("[RETENTION] Occurrence retention cleanup started");
        context.LogInformation($"Retention: Completed={settings.CompletedRetentionDays}d, Failed={settings.FailedRetentionDays}d, Cancelled={settings.CancelledRetentionDays}d, TimedOut={settings.TimedOutRetentionDays}d");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        // Status enum values: Queued=0, Running=1, Completed=2, Failed=3, Cancelled=4, TimedOut=5

        // 1. Delete old COMPLETED occurrences
        var (completedDeleted, completedLogsDeleted) = await DeleteOccurrencesByStatusAsync(
            connection, 2, settings.CompletedRetentionDays, settings.BatchSize, context);
        results["Completed"] = (completedDeleted, completedLogsDeleted);
        totalDeleted += completedDeleted;
        totalLogsDeleted += completedLogsDeleted;

        // 2. Delete old FAILED occurrences
        var (failedDeleted, failedLogsDeleted) = await DeleteOccurrencesByStatusAsync(
            connection, 3, settings.FailedRetentionDays, settings.BatchSize, context);
        results["Failed"] = (failedDeleted, failedLogsDeleted);
        totalDeleted += failedDeleted;
        totalLogsDeleted += failedLogsDeleted;

        // 3. Delete old CANCELLED occurrences
        var (cancelledDeleted, cancelledLogsDeleted) = await DeleteOccurrencesByStatusAsync(
            connection, 4, settings.CancelledRetentionDays, settings.BatchSize, context);
        results["Cancelled"] = (cancelledDeleted, cancelledLogsDeleted);
        totalDeleted += cancelledDeleted;
        totalLogsDeleted += cancelledLogsDeleted;

        // 4. Delete old TIMED OUT occurrences
        var (timedOutDeleted, timedOutLogsDeleted) = await DeleteOccurrencesByStatusAsync(
            connection, 5, settings.TimedOutRetentionDays, settings.BatchSize, context);
        results["TimedOut"] = (timedOutDeleted, timedOutLogsDeleted);
        totalDeleted += timedOutDeleted;
        totalLogsDeleted += timedOutLogsDeleted;

        context.LogInformation($"[DONE] Occurrence retention cleanup completed. Total deleted: {totalDeleted} occurrences, {totalLogsDeleted} logs");

        // OPTIONAL: Run VACUUM if enabled and threshold met
        if (settings.VacuumAfterCleanup && (totalDeleted >= settings.VacuumThreshold || totalLogsDeleted >= (settings.VacuumThreshold * 5)))
        {
            context.LogInformation($"[VACUUM] Running VACUUM ANALYZE to reclaim space (deleted {totalDeleted} occurrences, {totalLogsDeleted} logs)...");

            try
            {
                // VACUUM ANALYZE both tables to reclaim space and update statistics
                await connection.ExecuteAsync("VACUUM ANALYZE \"JobOccurrences\"");
                context.LogInformation("  [OK] VACUUM JobOccurrences completed");

                await connection.ExecuteAsync("VACUUM ANALYZE \"JobOccurrenceLogs\"");
                context.LogInformation("  [OK] VACUUM JobOccurrenceLogs completed");
            }
            catch (Exception ex)
            {
                context.LogWarning($"  [WARNING] VACUUM failed: {ex.Message} (non-critical, autovacuum will handle)");
            }
        }
        else
        {
            context.LogInformation($"[SKIP] VACUUM skipped (VacuumAfterCleanup={settings.VacuumAfterCleanup}, deleted={totalDeleted}, threshold={settings.VacuumThreshold})");
        }

        return JsonSerializer.Serialize(new
        {
            Success = true,
            TotalDeleted = totalDeleted,
            TotalLogsDeleted = totalLogsDeleted,
            Details = results
        });
    }

    private static async Task<(int occurrences, int logs)> DeleteOccurrencesByStatusAsync(
        NpgsqlConnection connection,
        int status,
        int retentionDays,
        int batchSize,
        IJobContext context)
    {
        var statusName = status switch
        {
            2 => "Completed",
            3 => "Failed",
            4 => "Cancelled",
            5 => "TimedOut",
            _ => $"Status{status}"
        };

        var cutoffDate = DateTime.UtcNow.AddDays(-retentionDays);
        var totalDeleted = 0;
        var totalLogsDeleted = 0;
        int deletedInBatch;

        context.LogInformation($"  Deleting {statusName} occurrences older than {cutoffDate:yyyy-MM-dd}...");

        // Delete in batches to avoid long locks
        do
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            // Logs and occurrences go in one statement, over one selected set, so neither can be removed without the other
            var sql = @"
                WITH to_delete AS (
                    SELECT ""Id"" FROM ""JobOccurrences""
                    WHERE ""Status"" = @Status
                    AND (
                        (""EndTime"" IS NOT NULL AND ""EndTime"" < @CutoffDate)
                        OR (""EndTime"" IS NULL AND ""CreatedAt"" < @CutoffDate)
                    )
                    LIMIT @BatchSize
                ),
                deleted_logs AS (
                    DELETE FROM ""JobOccurrenceLogs""
                    WHERE ""OccurrenceId"" IN (SELECT ""Id"" FROM to_delete)
                    RETURNING 1
                ),
                deleted_occurrences AS (
                    DELETE FROM ""JobOccurrences""
                    WHERE ""Id"" IN (SELECT ""Id"" FROM to_delete)
                    RETURNING 1
                )
                SELECT
                    (SELECT COUNT(*)::int FROM deleted_occurrences) AS Occurrences,
                    (SELECT COUNT(*)::int FROM deleted_logs) AS Logs";

            var deleted = await connection.QuerySingleAsync<(int Occurrences, int Logs)>(sql, new
            {
                Status = status,
                CutoffDate = cutoffDate,
                BatchSize = batchSize
            });

            deletedInBatch = deleted.Occurrences;
            var logsDeletedInBatch = deleted.Logs;

            totalDeleted += deletedInBatch;
            totalLogsDeleted += logsDeletedInBatch;

            if (deletedInBatch > 0)
            {
                context.LogInformation($"    Deleted batch: {deletedInBatch} occurrences, {logsDeletedInBatch} logs (total: {totalDeleted} occurrences, {totalLogsDeleted} logs)");
            }
        } while (deletedInBatch == batchSize);

        context.LogInformation($"  [OK] {statusName}: Deleted {totalDeleted} occurrences, {totalLogsDeleted} logs");

        return (totalDeleted, totalLogsDeleted);
    }
}
