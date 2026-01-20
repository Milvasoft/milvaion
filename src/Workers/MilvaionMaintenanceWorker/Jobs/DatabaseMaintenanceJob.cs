using Dapper;
using Microsoft.Extensions.Options;
using MilvaionMaintenanceWorker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Npgsql;
using System.Text.Json;

namespace MilvaionMaintenanceWorker.Jobs;

/// <summary>
/// Performs database maintenance operations: VACUUM, ANALYZE, and optionally REINDEX.
/// Should be scheduled during low-traffic periods (e.g., weekly at 3 AM Sunday).
/// </summary>
public class DatabaseMaintenanceJob(IOptions<MaintenanceOptions> options) : IAsyncJobWithResult
{
    private readonly MaintenanceOptions _options = options.Value;

    public async Task<string> ExecuteAsync(IJobContext context)
    {
        var settings = _options.DatabaseMaintenance;
        var results = new Dictionary<string, object>();
        var processedTables = new List<string>();

        context.LogInformation("[DB-MAINTENANCE] Database maintenance started");
        context.LogInformation($"Tables to maintain: {string.Join(", ", settings.Tables)}");
        context.LogInformation($"VACUUM: {settings.EnableVacuum}, ANALYZE: {settings.EnableAnalyze}, REINDEX: {settings.EnableReindex}");

        await using var connection = new NpgsqlConnection(_options.DatabaseConnectionString);
        await connection.OpenAsync(context.CancellationToken);

        foreach (var table in settings.Tables)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                context.LogInformation($"Processing table: {table}");

                // Get table size before maintenance
                var sizeBefore = await GetTableSizeAsync(connection, table);
                context.LogInformation($"Size before: {FormatBytes(sizeBefore)}");

                // ANALYZE - Update statistics for query planner
                if (settings.EnableAnalyze)
                {
                    context.LogInformation($"Running ANALYZE on {table}...");
                    await connection.ExecuteAsync($"ANALYZE \"{table}\"");
                    context.LogInformation($"ANALYZE completed!");
                }

                // VACUUM - Reclaim storage from dead tuples
                if (settings.EnableVacuum)
                {
                    context.LogInformation($"Running VACUUM on {table}...");
                    // Note: VACUUM cannot run inside a transaction, Dapper handles this
                    await connection.ExecuteAsync($"VACUUM \"{table}\"");
                    context.LogInformation($"VACUUM completed");
                }

                // REINDEX - Rebuild indexes (use with caution, locks table)
                if (settings.EnableReindex)
                {
                    context.LogInformation($"Running REINDEX on {table}...");
                    await connection.ExecuteAsync($"REINDEX TABLE \"{table}\"");
                    context.LogInformation($"REINDEX completed");
                }

                // Get table size after maintenance
                var sizeAfter = await GetTableSizeAsync(connection, table);
                var savedBytes = sizeBefore - sizeAfter;

                context.LogInformation($"  Size after: {FormatBytes(sizeAfter)} (saved: {FormatBytes(savedBytes)})");

                processedTables.Add(table);
                results[table] = new { SizeBefore = sizeBefore, SizeAfter = sizeAfter, SavedBytes = savedBytes };
            }
            catch (Exception ex)
            {
                context.LogWarning($"[WARNING] Error processing {table}: {ex.Message}");
                results[table] = new { Error = ex.Message };
            }
        }

        context.LogInformation($"[DONE] Database maintenance completed. Processed {processedTables.Count}/{settings.Tables.Count} tables");

        return JsonSerializer.Serialize(new
        {
            Success = true,
            ProcessedTables = processedTables.Count,
            TotalTables = settings.Tables.Count,
            Details = results
        });
    }

    private static async Task<long> GetTableSizeAsync(NpgsqlConnection connection, string table)
    {
        // Use string interpolation since table names come from config (trusted source)
        // pg_total_relation_size requires the table name as a regclass
        var sql = $"SELECT pg_total_relation_size('\"{table}\"'::regclass)";
        return await connection.ExecuteScalarAsync<long>(sql);
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
