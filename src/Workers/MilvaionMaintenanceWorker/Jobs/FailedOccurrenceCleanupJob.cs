using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Cleans up old failed occurrences from the DLQ (Dead Letter Queue) table.
/// These are occurrences that exceeded max retry attempts.
/// Recommended schedule: Weekly.
/// </summary>
public class FailedOccurrenceCleanupJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.FailedOccurrenceRetention;

        context.LogInformation("[DLQ-CLEANUP] Failed occurrence (DLQ) cleanup started");
        context.LogInformation($"Retention: {settings.RetentionDays} days, Batch size: {settings.BatchSize}");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var cutoffDate = DateTime.UtcNow.AddDays(-settings.RetentionDays);
        var totalDeleted = 0;
        int deletedInBatch;

        context.LogInformation($"Deleting failed occurrences older than {cutoffDate:yyyy-MM-dd}...");

        // Get count before deletion
        var countBefore = await connection.ExecuteScalarAsync<int>(
            @"SELECT COUNT(*) FROM ""FailedOccurrences"" WHERE ""FailedAt"" < @CutoffDate",
            new { CutoffDate = cutoffDate });

        context.LogInformation($"Found {countBefore} failed occurrences to delete");

        // Delete in batches
        do
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var sql = @"
                DELETE FROM ""FailedOccurrences""
                WHERE ""Id"" IN (
                    SELECT ""Id"" FROM ""FailedOccurrences""
                    WHERE ""FailedAt"" < @CutoffDate
                    LIMIT @BatchSize
                )";

            deletedInBatch = await connection.ExecuteAsync(sql, new
            {
                CutoffDate = cutoffDate,
                settings.BatchSize
            });

            totalDeleted += deletedInBatch;

            if (deletedInBatch > 0)
            {
                context.LogInformation($"  Deleted batch: {deletedInBatch} (total: {totalDeleted})");
            }
        } while (deletedInBatch == settings.BatchSize);

        context.LogInformation($"[DONE] Failed occurrence cleanup completed. Deleted: {totalDeleted}");

        return JsonSerializer.Serialize(new
        {
            Success = true,
            TotalDeleted = totalDeleted,
            CutoffDate = cutoffDate,
            settings.RetentionDays
        });
    }
}
