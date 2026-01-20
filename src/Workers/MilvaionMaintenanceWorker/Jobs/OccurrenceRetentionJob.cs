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
public class OccurrenceRetentionJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.OccurrenceRetention;
        var results = new Dictionary<string, int>();
        var totalDeleted = 0;

        context.LogInformation("[RETENTION] Occurrence retention cleanup started");
        context.LogInformation($"Retention: Completed={settings.CompletedRetentionDays}d, Failed={settings.FailedRetentionDays}d, Cancelled={settings.CancelledRetentionDays}d, TimedOut={settings.TimedOutRetentionDays}d");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        // Status enum values: Queued=0, Running=1, Completed=2, Failed=3, Cancelled=4, TimedOut=5

        // 1. Delete old COMPLETED occurrences
        var completedDeleted = await DeleteOccurrencesByStatusAsync(
            connection, 2, settings.CompletedRetentionDays, settings.BatchSize, context);
        results["Completed"] = completedDeleted;
        totalDeleted += completedDeleted;

        // 2. Delete old FAILED occurrences
        var failedDeleted = await DeleteOccurrencesByStatusAsync(
            connection, 3, settings.FailedRetentionDays, settings.BatchSize, context);
        results["Failed"] = failedDeleted;
        totalDeleted += failedDeleted;

        // 3. Delete old CANCELLED occurrences
        var cancelledDeleted = await DeleteOccurrencesByStatusAsync(
            connection, 4, settings.CancelledRetentionDays, settings.BatchSize, context);
        results["Cancelled"] = cancelledDeleted;
        totalDeleted += cancelledDeleted;

        // 4. Delete old TIMED OUT occurrences
        var timedOutDeleted = await DeleteOccurrencesByStatusAsync(
            connection, 5, settings.TimedOutRetentionDays, settings.BatchSize, context);
        results["TimedOut"] = timedOutDeleted;
        totalDeleted += timedOutDeleted;

        context.LogInformation($"[DONE] Occurrence retention cleanup completed. Total deleted: {totalDeleted}");

        return JsonSerializer.Serialize(new
        {
            Success = true,
            TotalDeleted = totalDeleted,
            Details = results
        });
    }

    private static async Task<int> DeleteOccurrencesByStatusAsync(
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
        int deletedInBatch;

        context.LogInformation($"  Deleting {statusName} occurrences older than {cutoffDate:yyyy-MM-dd}...");

        // Delete in batches to avoid long locks
        do
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var sql = @"
                DELETE FROM ""JobOccurrences""
                WHERE ""Id"" IN (
                    SELECT ""Id"" FROM ""JobOccurrences""
                    WHERE ""Status"" = @Status
                    AND (
                        (""EndTime"" IS NOT NULL AND ""EndTime"" < @CutoffDate)
                        OR (""EndTime"" IS NULL AND ""CreatedAt"" < @CutoffDate)
                    )
                    LIMIT @BatchSize
                )";

            deletedInBatch = await connection.ExecuteAsync(sql, new
            {
                Status = status,
                CutoffDate = cutoffDate,
                BatchSize = batchSize
            });

            totalDeleted += deletedInBatch;

            if (deletedInBatch > 0)
            {
                context.LogInformation($"    Deleted batch: {deletedInBatch} (total: {totalDeleted})");
            }
        } while (deletedInBatch == batchSize);

        context.LogInformation($"  [OK] {statusName}: Deleted {totalDeleted} occurrences");

        return totalDeleted;
    }
}
