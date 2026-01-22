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
        var tableSizesTask = Task.Run(async () =>
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
            return await GetTableSizesAsync(dbContext, cancellationToken);
        }, cancellationToken);

        var occurrenceGrowthTask = Task.Run(async () =>
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
            return await GetOccurrenceGrowthAsync(dbContext, cancellationToken);
        }, cancellationToken);

        var largeOccurrencesTask = Task.Run(async () =>
        {
            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();
            return await GetLargeOccurrencesAsync(dbContext, cancellationToken);
        }, cancellationToken);

        await Task.WhenAll(tableSizesTask, occurrenceGrowthTask, largeOccurrencesTask);

        var tableSizes = await tableSizesTask;
        var occurrenceGrowth = await occurrenceGrowthTask;
        var largeOccurrences = await largeOccurrencesTask;

        var totalSizeBytes = tableSizes.Sum(t => t.SizeBytes);

        var stats = new DatabaseStatisticsDto
        {
            TableSizes = tableSizes,
            OccurrenceGrowth = occurrenceGrowth,
            LargeOccurrences = largeOccurrences,
            TotalDatabaseSizeBytes = totalSizeBytes,
            TotalDatabaseSize = FormatBytes(totalSizeBytes)
        };

        return Response<DatabaseStatisticsDto>.Success(stats);
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

    private static async Task<List<TableSizeDto>> GetTableSizesAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
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

        return [.. tableSizesRaw.Select(t => new TableSizeDto
        {
            SchemaName = t.schemaname,
            TableName = t.tablename,
            Size = t.size,
            SizeBytes = t.size_bytes,
            Percentage = totalSizeBytes > 0 ? (decimal)t.size_bytes / totalSizeBytes * 100 : 0
        })];
    }

    private static async Task<List<OccurrenceGrowthDto>> GetOccurrenceGrowthAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        // Optimize: Only last 7 days instead of 30, use simpler aggregations
        var sql = @"
            SELECT 
                DATE_TRUNC('day', ""CreatedAt"") AS day,
                ""Status"" AS status,
                COUNT(*) AS count,
                CAST(AVG(CASE WHEN ""Exception"" IS NOT NULL THEN LENGTH(""Exception"") ELSE 0 END) AS INTEGER) AS avg_exception_size,
                CAST(AVG(CASE WHEN ""Logs"" IS NOT NULL THEN JSONB_ARRAY_LENGTH(""Logs"") ELSE 0 END) AS INTEGER) AS avg_log_count
            FROM ""JobOccurrences""
            WHERE ""CreatedAt"" > NOW() - INTERVAL '15 days'
            GROUP BY DATE_TRUNC('day', ""CreatedAt""), ""Status""
            ORDER BY day DESC
            LIMIT 50";

        var occurrenceGrowthRaw = await dbContext.Database.SqlQueryRaw<OccurrenceGrowthRawDto>(sql).ToListAsync(cancellationToken);

        return [.. occurrenceGrowthRaw.Select(o => new OccurrenceGrowthDto
        {
            Day = o.day,
            Status = o.status,
            Count = o.count,
            AvgExceptionSize = o.avg_exception_size,
            AvgLogCount = o.avg_log_count
        })];
    }

    private static async Task<List<LargeOccurrenceDto>> GetLargeOccurrencesAsync(MilvaionDbContext dbContext, CancellationToken cancellationToken)
    {
        // Optimize: Only check recent occurrences (last 30 days) and limit to top 5
        var sql = @"
            SELECT 
                ""Id"",
                ""JobName"",
                ""Status"",
                ""CreatedAt"",
                pg_column_size(COALESCE(""Logs"", '[]'::jsonb)) AS logs_size,
                pg_column_size(COALESCE(""Exception"", '')) AS exception_size,
                pg_column_size(COALESCE(""StatusChangeLogs"", '[]'::jsonb)) AS status_logs_size
            FROM ""JobOccurrences""
            WHERE ""CreatedAt"" > NOW() - INTERVAL '30 days'
            ORDER BY pg_column_size(COALESCE(""Logs"", '[]'::jsonb)) +
                     pg_column_size(COALESCE(""Exception"", '')) +
                     pg_column_size(COALESCE(""StatusChangeLogs"", '[]'::jsonb)) DESC
            LIMIT 5";

        var largeOccurrencesRaw = await dbContext.Database.SqlQueryRaw<LargeOccurrenceRawDto>(sql).ToListAsync(cancellationToken);

        return [.. largeOccurrencesRaw.Select(o => new LargeOccurrenceDto
        {
            Id = o.Id,
            JobName = o.JobName,
            Status = o.Status,
            CreatedAt = o.CreatedAt,
            LogsSize = o.logs_size,
            ExceptionSize = o.exception_size,
            StatusLogsSize = o.status_logs_size
        })];
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
/// Raw DTO for occurrence growth query (matches PostgreSQL column names).
/// </summary>
internal class OccurrenceGrowthRawDto
{
    public DateTime day { get; set; }
    public int status { get; set; }
    public int count { get; set; }
    public int? avg_exception_size { get; set; }
    public int? avg_log_count { get; set; }
}

/// <summary>
/// Raw DTO for large occurrence query (matches PostgreSQL column names).
/// </summary>
internal class LargeOccurrenceRawDto
{
    public Guid Id { get; set; }
    public string JobName { get; set; }
    public int Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public int logs_size { get; set; }
    public int exception_size { get; set; }
    public int status_logs_size { get; set; }
}
#pragma warning restore IDE1006 // Naming Styles
