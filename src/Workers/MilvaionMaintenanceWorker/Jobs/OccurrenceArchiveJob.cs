using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Archives old job occurrences to a dated archive table instead of deleting them.
/// Creates a new table for each archive run (e.g., JobOccurrences_Archive_2024_01).
/// Useful for compliance, auditing, or historical analysis.
/// Recommended schedule: Monthly (1st day of month, 04:00).
/// </summary>
public class OccurrenceArchiveJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.OccurrenceArchive;

        context.LogInformation("[ARCHIVE] Occurrence archive job started");
        context.LogInformation($"Archive occurrences older than {settings.ArchiveAfterDays} days");
        context.LogInformation($"Statuses to archive: {string.Join(", ", settings.StatusesToArchive)}");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        var cutoffDate = DateTime.UtcNow.AddDays(-settings.ArchiveAfterDays);
        var archiveTableName = GenerateArchiveTableName(settings.ArchiveTablePrefix);

        context.LogInformation($"Cutoff date: {cutoffDate:yyyy-MM-dd HH:mm:ss}");
        context.LogInformation($"Archive table: {archiveTableName}");

        // 1. First check if there are any records to archive
        var statusFilter = string.Join(", ", settings.StatusesToArchive);
        var countToArchive = await connection.ExecuteScalarAsync<int>($@"
            SELECT COUNT(*) FROM ""JobOccurrences""
            WHERE ""Status"" IN ({statusFilter})
            AND (
                (""EndTime"" IS NOT NULL AND ""EndTime"" < @CutoffDate)
                OR (""EndTime"" IS NULL AND ""CreatedAt"" < @CutoffDate)
            )", new { CutoffDate = cutoffDate });

        context.LogInformation($"Found {countToArchive} occurrences to archive");

        // 2. If nothing to archive, return early without creating table
        if (countToArchive == 0)
        {
            context.LogInformation("[DONE] No occurrences to archive");
            return JsonSerializer.Serialize(new
            {
                Success = true,
                ArchivedCount = 0,
                ArchiveTable = (string)null,
                Message = "No occurrences matched the archive criteria"
            });
        }

        // 3. Create archive table only if we have records to archive
        await CreateArchiveTableIfNotExistsAsync(connection, archiveTableName, context);

        // 4. Archive in batches
        var totalArchived = 0;
        int archivedInBatch;

        do
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            archivedInBatch = await ArchiveBatchAsync(connection, archiveTableName, settings.StatusesToArchive, cutoffDate, settings.BatchSize);

            totalArchived += archivedInBatch;

            if (archivedInBatch > 0)
            {
                context.LogInformation($"  Archived batch: {archivedInBatch} (total: {totalArchived})");
            }
        } while (archivedInBatch == settings.BatchSize);

        // 4. Get archive table size
        var archiveTableSize = await GetTableSizeAsync(connection, archiveTableName);

        context.LogInformation($"[DONE] Archive completed. Total archived: {totalArchived}");
        context.LogInformation($"Archive table size: {FormatBytes(archiveTableSize)}");

        // 5. Optionally create index on archive table
        if (settings.CreateIndexOnArchive && totalArchived > 0)
        {
            await CreateArchiveIndexesAsync(connection, archiveTableName, context);
        }

        return JsonSerializer.Serialize(new
        {
            Success = true,
            ArchivedCount = totalArchived,
            ArchiveTable = archiveTableName,
            ArchiveTableSize = archiveTableSize,
            CutoffDate = cutoffDate
        });
    }

    private static string GenerateArchiveTableName(string prefix)
    {
        var now = DateTime.UtcNow;
        return $"{prefix}_{now:yyyy}_{now:MM}";
    }

    private static async Task CreateArchiveTableIfNotExistsAsync(
        NpgsqlConnection connection,
        string archiveTableName,
        IJobContext context)
    {
        // Check if table exists
        var tableExists = await connection.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.tables 
                WHERE table_name = @TableName
            )", new { TableName = archiveTableName });

        if (tableExists)
        {
            context.LogInformation($"  Archive table {archiveTableName} already exists");
            return;
        }

        context.LogInformation($"  Creating archive table {archiveTableName}...");

        // Create table with same structure as JobOccurrences
        var createTableSql = $@"
            CREATE TABLE ""{archiveTableName}"" (
                ""Id"" uuid NOT NULL,
                ""JobId"" uuid NOT NULL,
                ""CorrelationId"" uuid NOT NULL,
                ""Status"" integer NOT NULL,
                ""WorkerId"" varchar(200),
                ""JobName"" varchar(200),
                ""ScheduledTime"" timestamp with time zone NOT NULL,
                ""StartTime"" timestamp with time zone,
                ""EndTime"" timestamp with time zone,
                ""DurationMs"" bigint,
                ""Result"" text,
                ""Exception"" text,
                ""Logs"" jsonb,
                ""StatusChangeLogs"" jsonb,
                ""LastHeartbeat"" timestamp with time zone,
                ""RetryCount"" integer NOT NULL DEFAULT 0,
                ""CreatedAt"" timestamp with time zone NOT NULL,
                ""ArchivedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                CONSTRAINT ""PK_{archiveTableName}"" PRIMARY KEY (""Id"")
            )";

        await connection.ExecuteAsync(createTableSql);
        context.LogInformation($"  [OK] Archive table created");
    }

    private static async Task<int> ArchiveBatchAsync(NpgsqlConnection connection,
                                                     string archiveTableName,
                                                     List<int> statuses,
                                                     DateTime cutoffDate,
                                                     int batchSize)
    {
        var statusFilter = string.Join(", ", statuses);

        // Use CTE to move data atomically
        var archiveSql = $@"
            WITH to_archive AS (
                SELECT ""Id"" FROM ""JobOccurrences""
                WHERE ""Status"" IN ({statusFilter})
                AND (
                    (""EndTime"" IS NOT NULL AND ""EndTime"" < @CutoffDate)
                    OR (""EndTime"" IS NULL AND ""CreatedAt"" < @CutoffDate)
                )
                LIMIT @BatchSize
            ),
            inserted AS (
                INSERT INTO ""{archiveTableName}"" (
                    ""Id"", ""JobId"", ""CorrelationId"", ""Status"", ""WorkerId"", ""JobName"",
                    ""ScheduledTime"", ""StartTime"", ""EndTime"", ""DurationMs"",
                    ""Result"", ""Exception"", ""Logs"", ""StatusChangeLogs"",
                    ""LastHeartbeat"", ""RetryCount"", ""CreatedAt"", ""ArchivedAt""
                )
                SELECT
                    jo.""Id"", jo.""JobId"", jo.""CorrelationId"", jo.""Status"", jo.""WorkerId"", jo.""JobName"",
                    jo.""ScheduledTime"", jo.""StartTime"", jo.""EndTime"", jo.""DurationMs"",
                    jo.""Result"", jo.""Exception"", jo.""Logs"", jo.""StatusChangeLogs"",
                    jo.""LastHeartbeat"", jo.""RetryCount"", jo.""CreatedAt"", NOW()
                FROM ""JobOccurrences"" jo
                INNER JOIN to_archive ta ON jo.""Id"" = ta.""Id""
                RETURNING ""Id""
            )
            DELETE FROM ""JobOccurrences""
            WHERE ""Id"" IN (SELECT ""Id"" FROM inserted)";

        return await connection.ExecuteAsync(archiveSql, new { CutoffDate = cutoffDate, BatchSize = batchSize });
    }

    private static async Task CreateArchiveIndexesAsync(
        NpgsqlConnection connection,
        string archiveTableName,
        IJobContext context)
    {
        context.LogInformation("  Creating indexes on archive table...");

        try
        {
            // Index on JobId for job-based queries
            await connection.ExecuteAsync($@"
                CREATE INDEX IF NOT EXISTS ""IX_{archiveTableName}_JobId""
                ON ""{archiveTableName}"" (""JobId"")");

            // Index on CorrelationId for tracing
            await connection.ExecuteAsync($@"
                CREATE INDEX IF NOT EXISTS ""IX_{archiveTableName}_CorrelationId""
                ON ""{archiveTableName}"" (""CorrelationId"")");

            // Index on EndTime for date-based queries
            await connection.ExecuteAsync($@"
                CREATE INDEX IF NOT EXISTS ""IX_{archiveTableName}_EndTime""
                ON ""{archiveTableName}"" (""EndTime"")");

            context.LogInformation("  [OK] Indexes created");
        }
        catch (Exception ex)
        {
            context.LogWarning($"  [WARNING] Failed to create indexes: {ex.Message}");
        }
    }

    private static async Task<long> GetTableSizeAsync(NpgsqlConnection connection, string table)
    {
        try
        {
            return await connection.ExecuteScalarAsync<long>(
                "SELECT pg_total_relation_size(@TableName)",
                new { TableName = table });
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }
}
