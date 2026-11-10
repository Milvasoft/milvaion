using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Archives old job occurrences to a dated archive table instead of deleting them.
/// Creates a new table for each archive run (e.g., JobOccurrences_Archive_2024_01).
/// Useful for compliance, auditing, or historical analysis.
/// Recommended schedule: Monthly (1st day of month, 04:00).
/// </summary>
public class OccurrenceArchiveJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult<string>
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
        var occurrenceColumns = await EnsureArchiveTableAsync(connection, "JobOccurrences", archiveTableName, context);

        // 3.1. Also create archive table for logs
        var archiveLogsTableName = $"{archiveTableName}_Logs";
        var logColumns = await EnsureArchiveTableAsync(connection, "JobOccurrenceLogs", archiveLogsTableName, context);

        // 4. Archive in batches
        var totalArchived = 0;
        var totalLogsArchived = 0;
        int archivedInBatch;

        do
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var (archived, logsArchived) = await ArchiveBatchAsync(connection, archiveTableName, archiveLogsTableName, occurrenceColumns, logColumns, settings.StatusesToArchive, cutoffDate, settings.BatchSize);

            totalArchived += archived;
            totalLogsArchived += logsArchived;
            archivedInBatch = archived;

            if (archivedInBatch > 0)
            {
                context.LogInformation($"  Archived batch: {archivedInBatch} occurrences, {logsArchived} logs (total: {totalArchived} occurrences, {totalLogsArchived} logs)");
            }
        } while (archivedInBatch == settings.BatchSize);

        // 4. Get archive table size
        var archiveTableSize = await GetTableSizeAsync(connection, archiveTableName);
        var archiveLogsTableSize = await GetTableSizeAsync(connection, archiveLogsTableName);

        context.LogInformation($"[DONE] Archive completed. Total archived: {totalArchived} occurrences, {totalLogsArchived} logs");
        context.LogInformation($"Archive table size: {FormatBytes(archiveTableSize)}");
        context.LogInformation($"Archive logs table size: {FormatBytes(archiveLogsTableSize)}");

        // 5. Optionally create index on archive table
        if (settings.CreateIndexOnArchive && totalArchived > 0)
        {
            await CreateArchiveIndexesAsync(connection, archiveTableName, archiveLogsTableName, context);
        }

        // 6. OPTIONAL: Run VACUUM if enabled and threshold met
        if (settings.VacuumAfterArchive && (totalArchived >= settings.VacuumThreshold || totalLogsArchived >= (settings.VacuumThreshold * 5)))
        {
            context.LogInformation($"[VACUUM] Running VACUUM ANALYZE to reclaim space (archived {totalArchived} occurrences, {totalLogsArchived} logs)...");

            try
            {
                // VACUUM ANALYZE both tables to reclaim space after archive
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
            context.LogInformation($"[SKIP] VACUUM skipped (VacuumAfterArchive={settings.VacuumAfterArchive}, archived={totalArchived}, threshold={settings.VacuumThreshold})");
        }

        return JsonSerializer.Serialize(new
        {
            Success = true,
            ArchivedCount = totalArchived,
            ArchivedLogsCount = totalLogsArchived,
            ArchiveTable = archiveTableName,
            ArchiveLogsTable = archiveLogsTableName,
            ArchiveTableSize = archiveTableSize,
            ArchiveLogsTableSize = archiveLogsTableSize,
            CutoffDate = cutoffDate
        });
    }

    private static string GenerateArchiveTableName(string prefix)
    {
        var now = DateTime.UtcNow;
        return $"{prefix}_{now:yyyy}_{now:MM}";
    }

    /// <summary>
    /// Creates the archive table from the live table's current definition and returns the columns to copy.
    /// A table from an earlier run of the same month gets any columns added to the live table since.
    /// </summary>
    private static async Task<string> EnsureArchiveTableAsync(NpgsqlConnection connection,
                                                              string sourceTableName,
                                                              string archiveTableName,
                                                              IJobContext context)
    {
        // Earlier versions created these tables with a column that never existed on JobOccurrences and a Level of the
        // wrong type, so every insert failed and they can only be empty. They cannot receive rows and have to be rebuilt.
        var hasLegacySchema = await connection.ExecuteScalarAsync<bool>(@"
            SELECT EXISTS (
                SELECT FROM information_schema.columns
                WHERE table_name = @TableName
                AND (column_name = 'ScheduledTime' OR (column_name = 'Level' AND data_type = 'integer'))
            )", new { TableName = archiveTableName });

        if (hasLegacySchema)
        {
            var legacyRows = await connection.ExecuteScalarAsync<long>($@"SELECT COUNT(*) FROM ""{archiveTableName}""");

            if (legacyRows > 0)
                throw new PermanentJobException($"Archive table {archiveTableName} has an outdated schema and contains {legacyRows} rows. Rename it before archiving again.");

            await connection.ExecuteAsync($@"DROP TABLE ""{archiveTableName}""");

            context.LogWarning($"  Replaced empty archive table {archiveTableName} that had an outdated schema");
        }

        await connection.ExecuteAsync($@"
            CREATE TABLE IF NOT EXISTS ""{archiveTableName}"" (
                LIKE ""{sourceTableName}"" INCLUDING DEFAULTS,
                ""ArchivedAt"" timestamp with time zone NOT NULL DEFAULT NOW(),
                CONSTRAINT ""PK_{archiveTableName}"" PRIMARY KEY (""Id"")
            )");

        var columns = (await connection.QueryAsync<(string Name, string Type)>(@"
            SELECT quote_ident(attname) AS Name, format_type(atttypid, atttypmod) AS Type
            FROM pg_attribute
            WHERE attrelid = quote_ident(@TableName)::regclass AND attnum > 0 AND NOT attisdropped
            ORDER BY attnum", new { TableName = sourceTableName })).ToList();

        foreach (var (name, type) in columns)
            await connection.ExecuteAsync($@"ALTER TABLE ""{archiveTableName}"" ADD COLUMN IF NOT EXISTS {name} {type}");

        context.LogInformation($"  [OK] Archive table {archiveTableName} is ready");

        return string.Join(", ", columns.Select(c => c.Name));
    }

    private static async Task<(int archivedOccurrences, int archivedLogs)> ArchiveBatchAsync(
        NpgsqlConnection connection,
        string archiveTableName,
        string archiveLogsTableName,
        string occurrenceColumns,
        string logColumns,
        List<int> statuses,
        DateTime cutoffDate,
        int batchSize)
    {
        var statusFilter = string.Join(", ", statuses);

        // One statement over one selected set: rows are removed only together with their archived copies. SKIP LOCKED
        // leaves occurrences the status tracker is still updating for the next batch.
        var archiveSql = $@"
            WITH to_archive AS (
                SELECT ""Id"" FROM ""JobOccurrences""
                WHERE ""Status"" IN ({statusFilter})
                AND (
                    (""EndTime"" IS NOT NULL AND ""EndTime"" < @CutoffDate)
                    OR (""EndTime"" IS NULL AND ""CreatedAt"" < @CutoffDate)
                )
                ORDER BY ""Id""
                LIMIT @BatchSize
                FOR UPDATE SKIP LOCKED
            ),
            deleted_logs AS (
                DELETE FROM ""JobOccurrenceLogs""
                WHERE ""OccurrenceId"" IN (SELECT ""Id"" FROM to_archive)
                RETURNING *
            ),
            archived_logs AS (
                INSERT INTO ""{archiveLogsTableName}"" ({logColumns})
                SELECT {logColumns} FROM deleted_logs
                RETURNING 1
            ),
            deleted_occurrences AS (
                DELETE FROM ""JobOccurrences""
                WHERE ""Id"" IN (SELECT ""Id"" FROM to_archive)
                RETURNING *
            ),
            archived_occurrences AS (
                INSERT INTO ""{archiveTableName}"" ({occurrenceColumns})
                SELECT {occurrenceColumns} FROM deleted_occurrences
                RETURNING 1
            )
            SELECT
                (SELECT COUNT(*)::int FROM archived_occurrences) AS archived_occurrences,
                (SELECT COUNT(*)::int FROM archived_logs) AS archived_logs";

        return await connection.QuerySingleAsync<(int, int)>(archiveSql, new { CutoffDate = cutoffDate, BatchSize = batchSize });
    }

    private static async Task CreateArchiveIndexesAsync(
        NpgsqlConnection connection,
        string archiveTableName,
        string archiveLogsTableName,
        IJobContext context)
    {
        context.LogInformation("  Creating indexes on archive tables...");

        try
        {
            // Indexes for JobOccurrences archive table
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

            // Indexes for JobOccurrenceLogs archive table
            // Index on OccurrenceId for joining with occurrences
            await connection.ExecuteAsync($@"
                CREATE INDEX IF NOT EXISTS ""IX_{archiveLogsTableName}_OccurrenceId""
                ON ""{archiveLogsTableName}"" (""OccurrenceId"")");

            // Index on Timestamp for time-based queries
            await connection.ExecuteAsync($@"
                CREATE INDEX IF NOT EXISTS ""IX_{archiveLogsTableName}_Timestamp""
                ON ""{archiveLogsTableName}"" (""Timestamp"")");

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
                "SELECT pg_total_relation_size(quote_ident(@TableName)::regclass)",
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
