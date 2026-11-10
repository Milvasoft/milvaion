using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Enums;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.Redis;
using Milvaion.Infrastructure.Services.Redis.Utils;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Interception.Interceptors.Cache;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Implementation of dispatcher control service.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="DispatcherControlService"/> class.
/// </remarks>
public class AdminService(IServiceProvider serviceProvider) : IAdminService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;

    /// <summary>
    /// Gets queue statistics for all queues.
    /// /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Queue statistics</returns>
    public async Task<Response<List<QueueStats>>> GetQueueStatsAsync(CancellationToken cancellationToken)
    {
        var queueMonitor = _serviceProvider.GetRequiredService<IQueueDepthMonitor>();

        var stats = await queueMonitor.GetAllQueueStatsAsync(cancellationToken);

        return Response<List<QueueStats>>.Success(stats);
    }

    /// <summary>
    /// Gets detailed information about a specific queue.
    /// </summary>
    /// <param name="queueName">Queue name</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Queue depth information</returns>
    public async Task<Response<QueueDepthInfo>> GetQueueInfoAsync(string queueName, CancellationToken cancellationToken)
    {
        var queueMonitor = _serviceProvider.GetRequiredService<IQueueDepthMonitor>();

        var info = await queueMonitor.GetQueueDepthAsync(queueName, cancellationToken);

        return Response<QueueDepthInfo>.Success(info);
    }

    /// <summary>
    /// Gets system health overview including dispatcher status.
    /// </summary>
    /// <returns>System health information</returns>
    public async Task<Response<SystemHealthInfo>> GetSystemHealthAsync(CancellationToken cancellationToken)
    {
        var queueMonitor = _serviceProvider.GetRequiredService<IQueueDepthMonitor>();

        var queueStats = await queueMonitor.GetAllQueueStatsAsync(cancellationToken);

        // Use runtime control service instead of config
        var dispatcherControl = _serviceProvider.GetRequiredService<IDispatcherControlService>();

        await dispatcherControl.RefreshAsync(cancellationToken);

        var dispatcherEnabled = dispatcherControl.IsEnabled;

        // Resolve scoped repository inside a scope
        int activeJobCount;

        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var jobRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<ScheduledJob>>();

            activeJobCount = await jobRepository.GetCountAsync(j => j.IsActive, cancellationToken: cancellationToken);
        }

        var health = new SystemHealthInfo
        {
            DispatcherEnabled = dispatcherEnabled,
            TotalActiveJobs = activeJobCount,
            QueueStats = queueStats,
            OverallHealth = DetermineOverallHealth(queueStats, dispatcherEnabled),
            Timestamp = DateTime.UtcNow
        };

        return Response<SystemHealthInfo>.Success(health);
    }

    /// <summary>
    /// Emergency stop - Disables the job dispatcher at runtime.
    /// </summary>
    /// <param name="reason">Reason for emergency stop</param>
    /// <returns>Success response</returns>
    public IResponse EmergencyStop(string reason)
    {
        var httpContextAccessor = _serviceProvider.GetRequiredService<IHttpContextAccessor>();

        var username = httpContextAccessor.HttpContext.CurrentUserName() ?? "Unknown";

        var dispatcherControl = _serviceProvider.GetRequiredService<IDispatcherControlService>();

        dispatcherControl.Stop(reason, username);

        return Response.Success("Emergency stop activated. Job dispatcher has been paused. No new jobs will be dispatched until manually resumed.");
    }

    /// <summary>
    /// Resume operations - Enables the job dispatcher at runtime.
    /// </summary>
    /// <returns>Success response</returns>
    public IResponse ResumeOperations()
    {
        var httpContextAccessor = _serviceProvider.GetRequiredService<IHttpContextAccessor>();

        var username = httpContextAccessor.HttpContext.CurrentUserName() ?? "Unknown";

        var dispatcherControl = _serviceProvider.GetRequiredService<IDispatcherControlService>();

        dispatcherControl.Resume(username);

        return Response.Success("System resumed. Job dispatcher will continue processing jobs.");
    }

    /// <summary>
    /// Gets job statistics grouped by status.
    /// /// </summary>
    /// <returns>Job statistics</returns>
    public async Task<Response<JobStatistics>> GetJobStatisticsAsync(CancellationToken cancellationToken)
    {
        List<ScheduledJob> allJobs;

        await using (var scope = _serviceProvider.CreateAsyncScope())
        {
            var jobRepository = scope.ServiceProvider.GetRequiredService<IMilvaionRepositoryBase<ScheduledJob>>();

            allJobs = await jobRepository.GetAllAsync(projection: j => new ScheduledJob
            {
                Id = j.Id,
                IsActive = j.IsActive,
                CronExpression = j.CronExpression
            }, cancellationToken: cancellationToken);
        }

        var stats = new JobStatistics
        {
            TotalJobs = allJobs.Count,
            ActiveJobs = allJobs.Count(j => j.IsActive),
            InactiveJobs = allJobs.Count(j => !j.IsActive),
            RecurringJobs = allJobs.Count(j => !string.IsNullOrEmpty(j.CronExpression)),
            OneTimeJobs = allJobs.Count(j => string.IsNullOrEmpty(j.CronExpression))
        };

        return Response<JobStatistics>.Success(stats);
    }

    /// <summary>
    /// Gets Redis circuit breaker statistics.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Circuit breaker statistics</returns>
    public Response<RedisCircuitBreakerStatsDto> GetRedisCircuitBreakerStats(CancellationToken cancellationToken)
    {
        var circuitBreaker = _serviceProvider.GetService<IRedisCircuitBreaker>();

        if (circuitBreaker == null)
            return Response<RedisCircuitBreakerStatsDto>.Error(default, "Redis circuit breaker is not configured");

        var stats = circuitBreaker.GetStats();

        var dto = new RedisCircuitBreakerStatsDto
        {
            State = stats.State.ToString(),
            FailureCount = stats.FailureCount,
            LastFailureTime = stats.LastFailureTime,
            TotalOperations = stats.TotalOperations,
            TotalFailures = stats.TotalFailures,
            SuccessRatePercentage = stats.SuccessRate * 100,
            HealthStatus = GetHealthStatus(stats.State),
            HealthMessage = GetHealthMessage(stats.State, stats.FailureCount, stats.LastFailureTime),
            TimeSinceLastFailure = GetTimeSinceLastFailure(stats.LastFailureTime),
            Recommendation = GetRecommendation(stats.State, stats.FailureCount)
        };

        return Response<RedisCircuitBreakerStatsDto>.Success(dto);
    }

    /// <summary>
    /// Gets database statistics including table sizes, occurrence growth, and large occurrences.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Database statistics</returns>
    [Cache(CacheConstant.Key.DatabaseStats, CacheConstant.Time.Seconds120)]
    public async Task<Response<DatabaseStatisticsDto>> GetDatabaseStatisticsAsync(CancellationToken cancellationToken)
    {
        // Run all queries in parallel with separate DbContext instances for each
        var tableSizesTask = GetTableSizesAsync(cancellationToken);
        var indexEfficiencyTask = GetIndexEfficiencyAsync(cancellationToken);
        var cacheHitRatioTask = GetCacheHitRatioAsync(cancellationToken);
        var tableBloatTask = GetTableBloatAsync(cancellationToken);

        await Task.WhenAll(tableSizesTask, indexEfficiencyTask, cacheHitRatioTask, tableBloatTask);

        var tableSizesResponse = await tableSizesTask;
        var indexEfficiencyResponse = await indexEfficiencyTask;
        var cacheHitRatioResponse = await cacheHitRatioTask;
        var tableBloatResponse = await tableBloatTask;

        var tableSizes = tableSizesResponse.Data ?? [];
        var totalSizeBytes = tableSizes.Sum(t => t.SizeBytes);

        var stats = new DatabaseStatisticsDto
        {
            TableSizes = tableSizes,
            TotalDatabaseSizeBytes = totalSizeBytes,
            TotalDatabaseSize = FormatBytes(totalSizeBytes),
            IndexEfficiency = indexEfficiencyResponse.Data,
            CacheHitRatio = cacheHitRatioResponse.Data,
            TableBloat = tableBloatResponse.Data
        };

        return Response<DatabaseStatisticsDto>.Success(stats);
    }

    /// <summary>
    /// Gets top tables by size.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Table sizes</returns>
    [Cache(CacheConstant.Key.DatabaseStats + ":tables", CacheConstant.Time.Seconds120)]
    public async Task<Response<List<TableSizeDto>>> GetTableSizesAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
        var sql = @"
            SELECT 
                schemaname,
                tablename,
                pg_size_pretty(pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(tablename))) AS size,
                pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(tablename)) AS size_bytes
            FROM pg_tables
            WHERE schemaname = 'public'
              AND tablename NOT LIKE 'pg_%'
              AND tablename NOT LIKE 'sql_%'
              AND tablename != '_EfMigrationHistory'
              AND tablename != '_MigrationHistory'
            ORDER BY size_bytes DESC
            LIMIT 10";

        var tableSizesRaw = await dbContext.Database.SqlQueryRaw<TableSizeRawDto>(sql).ToListAsync(cancellationToken);

        var totalSizeBytes = tableSizesRaw.Sum(t => t.size_bytes);

        List<TableSizeDto> tableSizes = [.. tableSizesRaw.Select(t => new TableSizeDto
        {
            SchemaName = t.schemaname,
            TableName = t.tablename,
            Size = t.size,
            SizeBytes = t.size_bytes,
            Percentage = totalSizeBytes > 0 ? (decimal)t.size_bytes / totalSizeBytes * 100 : 0
        })];

        return Response<List<TableSizeDto>>.Success(tableSizes);
    }

    /// <summary>
    /// Gets index efficiency statistics (unused/underutilized indexes).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Index efficiency statistics</returns>
    [Cache(CacheConstant.Key.DatabaseStats + ":indexes", CacheConstant.Time.Seconds120)]
    public async Task<Response<IndexEfficiencyDto>> GetIndexEfficiencyAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Find unused or rarely used indexes (excluding primary keys and unique constraints)
        var sql = @"
            SELECT 
                ui.schemaname || '.' || ui.relname AS table_name,
                ui.indexrelname AS index_name,
                pg_relation_size(ui.indexrelid) AS size_bytes,
                ui.idx_scan AS scans,
                ui.idx_tup_read AS tuples_read
            FROM pg_stat_user_indexes ui
            JOIN pg_index i ON ui.indexrelid = i.indexrelid
            WHERE ui.schemaname = 'public'
              AND ui.indexrelname NOT LIKE 'pg_%'
              AND ui.indexrelname NOT LIKE 'PK_%'
              AND ui.indexrelname NOT LIKE 'AK_%'
              AND i.indisprimary = false
              AND i.indisunique = false
              AND ui.relname IN ('JobOccurrences', 'JobOccurrenceLogs', 'ScheduledJobs', 'FailedOccurrences')
            ORDER BY pg_relation_size(ui.indexrelid) DESC";

        var indexesRaw = await dbContext.Database.SqlQueryRaw<IndexStatsRawDto>(sql).ToListAsync(cancellationToken);

        var indexes = indexesRaw.Select(i =>
        {
            var efficiencyScore = i.scans > 0 ? Math.Min(100, (i.scans / 1000.0m) * 100) : 0;
            var status = i.scans == 0 ? "Unused" : i.scans < 100 ? "Rarely Used" : "Normal";

            return new IndexStatsDto
            {
                TableName = i.table_name,
                IndexName = i.index_name,
                SizeBytes = i.size_bytes,
                Size = FormatBytes(i.size_bytes),
                Scans = i.scans,
                TuplesRead = i.tuples_read,
                EfficiencyScore = efficiencyScore,
                Status = status
            };
        }).ToList();

        var unusedIndexes = indexes.Where(i => i.Status is "Unused" or "Rarely Used").ToList();
        var totalWastedBytes = unusedIndexes.Sum(i => i.SizeBytes);

        var recommendation = totalWastedBytes > 1024 * 1024 * 100 // 100 MB
            ? $"Consider dropping {unusedIndexes.Count} unused/rarely used indexes to free up {FormatBytes(totalWastedBytes)}."
            : "Index usage is optimal. No action needed.";

        var indexEfficiency = new IndexEfficiencyDto
        {
            Indexes = [.. unusedIndexes.OrderByDescending(i => i.SizeBytes)],
            TotalWastedBytes = totalWastedBytes,
            TotalWastedSpace = FormatBytes(totalWastedBytes),
            Recommendation = recommendation
        };

        return Response<IndexEfficiencyDto>.Success(indexEfficiency);
    }

    /// <summary>
    /// Gets database cache hit ratio.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Cache hit ratio statistics</returns>
    [Cache(CacheConstant.Key.DatabaseStats + ":cache", CacheConstant.Time.Seconds120)]
    public async Task<Response<CacheHitRatioDto>> GetCacheHitRatioAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
        // Cache hit ratio for tables and indexes
        var sql = @"
            SELECT 
                SUM(heap_blks_read) AS disk_reads,
                SUM(heap_blks_hit) AS cache_reads,
                SUM(idx_blks_read) AS idx_disk_reads,
                SUM(idx_blks_hit) AS idx_cache_reads
            FROM pg_statio_user_tables";

        var result = await dbContext.Database.SqlQueryRaw<CacheHitRatioRawDto>(sql).FirstOrDefaultAsync(cancellationToken);

        if (result == null)
        {
            return Response<CacheHitRatioDto>.Success(new CacheHitRatioDto
            {
                Status = "Unknown",
                Recommendation = "No data available"
            });
        }

        var totalReads = result.disk_reads + result.cache_reads;
        var hitRatio = totalReads > 0 ? (decimal)result.cache_reads / totalReads * 100 : 0;

        var totalIdxReads = result.idx_disk_reads + result.idx_cache_reads;
        var idxHitRatio = totalIdxReads > 0 ? (decimal)result.idx_cache_reads / totalIdxReads * 100 : 0;

        var tableReads = result.disk_reads + result.cache_reads;
        var tableHitRatio = tableReads > 0 ? (decimal)result.cache_reads / tableReads * 100 : 0;

        var status = hitRatio switch
        {
            >= 99 => "Excellent",
            >= 95 => "Good",
            >= 90 => "Poor",
            _ => "Critical"
        };

        var recommendation = status switch
        {
            "Excellent" => "Cache performance is excellent. Database is well-tuned.",
            "Good" => "Cache performance is acceptable. Monitor disk I/O.",
            "Poor" => "Consider increasing shared_buffers in PostgreSQL configuration.",
            _ => "CRITICAL: Cache hit ratio is too low. Increase shared_buffers and investigate slow queries."
        };

        var cacheHitRatio = new CacheHitRatioDto
        {
            HitRatioPercentage = hitRatio,
            IndexHitRatioPercentage = idxHitRatio,
            TableHitRatioPercentage = tableHitRatio,
            DiskReads = result.disk_reads,
            CacheReads = result.cache_reads,
            Status = status,
            Recommendation = recommendation
        };

        return Response<CacheHitRatioDto>.Success(cacheHitRatio);
    }

    /// <summary>
    /// Gets table bloat detection (VACUUM recommendation).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Table bloat statistics</returns>
    [Cache(CacheConstant.Key.DatabaseStats + ":bloat", CacheConstant.Time.Seconds120)]
    public async Task<Response<TableBloatDto>> GetTableBloatAsync(CancellationToken cancellationToken)
    {
        await using var scope = _serviceProvider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Estimate table bloat using pg_stat_user_tables
        var sql = @"
            SELECT 
                schemaname || '.' || relname AS table_name,
                pg_total_relation_size(quote_ident(schemaname) || '.' || quote_ident(relname)) AS actual_size_bytes,
                n_dead_tup AS dead_tuples,
                n_live_tup AS live_tuples,
                last_vacuum,
                last_analyze
            FROM pg_stat_user_tables
            WHERE schemaname = 'public'
              AND n_dead_tup > 1000
            ORDER BY n_dead_tup DESC
            LIMIT 10";

        var bloatedTablesRaw = await dbContext.Database.SqlQueryRaw<BloatedTableRawDto>(sql).ToListAsync(cancellationToken);

        var bloatedTables = bloatedTablesRaw.Select(t =>
        {
            var totalTuples = t.live_tuples + t.dead_tuples;
            var bloatPercentage = totalTuples > 0 ? (decimal)t.dead_tuples / totalTuples * 100 : 0;
            var expectedSizeBytes = totalTuples > 0 ? (long)(t.actual_size_bytes * ((decimal)t.live_tuples / totalTuples)) : t.actual_size_bytes;
            var wastedBytes = t.actual_size_bytes - expectedSizeBytes;

            var status = bloatPercentage switch
            {
                >= 50 => "Critical",
                >= 20 => "Warning",
                _ => "Normal"
            };

            return new BloatedTableDto
            {
                TableName = t.table_name,
                ActualSizeBytes = t.actual_size_bytes,
                ActualSize = FormatBytes(t.actual_size_bytes),
                ExpectedSizeBytes = expectedSizeBytes,
                ExpectedSize = FormatBytes(expectedSizeBytes),
                WastedBytes = wastedBytes,
                WastedSpace = FormatBytes(wastedBytes),
                BloatPercentage = bloatPercentage,
                DeadTuples = t.dead_tuples,
                LiveTuples = t.live_tuples,
                LastVacuum = t.last_vacuum,
                LastAnalyze = t.last_analyze,
                Status = status
            };
        }).ToList();

        var totalWastedBytes = bloatedTables.Sum(t => t.WastedBytes);

        var criticalTables = bloatedTables.Count(t => t.Status == "Critical");
        var recommendation = criticalTables > 0
            ? $"URGENT: {criticalTables} table(s) need VACUUM. Run 'VACUUM ANALYZE' on bloated tables to reclaim {FormatBytes(totalWastedBytes)}."
            : totalWastedBytes > 1024 * 1024 * 50 // 50 MB
                ? $"Consider running VACUUM on tables with high bloat to reclaim {FormatBytes(totalWastedBytes)}."
                : "Table bloat is under control. No action needed.";

        var tableBloat = new TableBloatDto
        {
            BloatedTables = bloatedTables,
            TotalWastedBytes = totalWastedBytes,
            TotalWastedSpace = FormatBytes(totalWastedBytes),
            Recommendation = recommendation
        };

        return Response<TableBloatDto>.Success(tableBloat);
    }

    /// <summary>
    /// Gets background service memory diagnostics.
    /// </summary>
    /// <returns>Database statistics</returns>
    public Response<AggregatedMemoryStats> GetBackgroundServiceMemoryDiagnostics()
    {
        var memoryStatsRegistry = _serviceProvider.GetRequiredService<IMemoryStatsRegistry>();

        return Response<AggregatedMemoryStats>.Success(memoryStatsRegistry.GetAggregatedStats());
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:0.##} {suffixes[suffixIndex]}";
    }

    private static string GetHealthStatus(CircuitState state) => state switch
    {
        CircuitState.Closed => "Healthy",
        CircuitState.HalfOpen => "Warning",
        CircuitState.Open => "Critical",
        _ => "Unknown"
    };

    private static string GetHealthMessage(CircuitState state, int failureCount, DateTime? lastFailureTime) => state switch
    {
        CircuitState.Closed when failureCount == 0 => "Redis is operating normally. No recent failures detected.",
        CircuitState.Closed => $"Redis is operational. {failureCount} consecutive failure(s) detected, but below threshold.",
        CircuitState.HalfOpen => "Redis circuit is testing recovery. Allowing limited traffic to check if service has recovered.",
        CircuitState.Open => $"Redis circuit is OPEN! All Redis operations are being blocked. Last failure: {lastFailureTime?.ToString("yyyy-MM-dd HH:mm:ss UTC")}",
        _ => "Unknown circuit state"
    };

    private static string GetTimeSinceLastFailure(DateTime? lastFailureTime)
    {
        if (!lastFailureTime.HasValue)
            return "No failures recorded";

        var timeSince = DateTime.UtcNow - lastFailureTime.Value;

        if (timeSince.TotalMinutes < 1)
            return $"{timeSince.Seconds} seconds ago";
        if (timeSince.TotalHours < 1)
            return $"{timeSince.Minutes} minutes ago";
        if (timeSince.TotalDays < 1)
            return $"{timeSince.Hours} hours ago";

        return $"{timeSince.Days} days ago";
    }

    private static string GetRecommendation(CircuitState state, int failureCount) => state switch
    {
        CircuitState.Closed when failureCount == 0 => "No action required. System is healthy.",
        CircuitState.Closed => "Monitor Redis connection. Consider investigating if failures continue.",
        CircuitState.HalfOpen => "Circuit is testing recovery. Wait for automatic state transition.",
        CircuitState.Open => "URGENT: Check Redis container status, network connectivity, and logs immediately. System is in degraded state.",
        _ => "Unknown state - manual investigation required"
    };

    /// <summary>
    /// Determines overall system health based on queue statistics and dispatcher status.
    /// </summary>
    /// <param name="queueStats"></param>
    /// <param name="dispatcherEnabled"></param>
    /// <returns></returns>
    private static SystemHealth DetermineOverallHealth(List<QueueStats> queueStats, bool dispatcherEnabled)
    {
        if (!dispatcherEnabled)
            return SystemHealth.Degraded;

        var hasCritical = queueStats.Any(q => q.HealthStatus == QueueHealthStatus.Critical);

        if (hasCritical)
            return SystemHealth.Critical;

        var hasWarning = queueStats.Any(q => q.HealthStatus == QueueHealthStatus.Warning);

        if (hasWarning)
            return SystemHealth.Warning;

        return SystemHealth.Healthy;
    }
}

/// <summary>
/// Raw DTO for table size query (matches PostgreSQL column names).
/// </summary>
#pragma warning disable IDE1006 // Naming Styles (matches PostgreSQL column names)
internal class TableSizeRawDto
{
    public string schemaname { get; set; }
    public string tablename { get; set; }
    public string size { get; set; }
    public long size_bytes { get; set; }
}

/// <summary>
/// Raw DTO for index stats query (matches PostgreSQL column names).
/// </summary>
internal class IndexStatsRawDto
{
    public string table_name { get; set; }
    public string index_name { get; set; }
    public long size_bytes { get; set; }
    public long scans { get; set; }
    public long tuples_read { get; set; }
}

/// <summary>
/// Raw DTO for cache hit ratio query (matches PostgreSQL column names).
/// </summary>
internal class CacheHitRatioRawDto
{
    public long disk_reads { get; set; }
    public long cache_reads { get; set; }
    public long idx_disk_reads { get; set; }
    public long idx_cache_reads { get; set; }
}

/// <summary>
/// Raw DTO for bloated table query (matches PostgreSQL column names).
/// </summary>
internal class BloatedTableRawDto
{
    public string table_name { get; set; }
    public long actual_size_bytes { get; set; }
    public long dead_tuples { get; set; }
    public long live_tuples { get; set; }
    public DateTime? last_vacuum { get; set; }
    public DateTime? last_analyze { get; set; }
}
#pragma warning restore IDE1006 // Naming Styles
