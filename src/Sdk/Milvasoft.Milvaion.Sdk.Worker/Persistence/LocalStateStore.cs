using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.Persistence;

/// <summary>
/// Interface for local state store operations.
/// </summary>
public interface ILocalStateStore : IDisposable, IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task StoreStatusUpdateAsync(Guid correlationId, Guid jobId, string workerId, JobOccurrenceStatus status, DateTime? startTime = null, DateTime? endTime = null, long? durationMs = null, string result = null, string exception = null, CancellationToken cancellationToken = default);
    Task<List<StoredStatusUpdate>> GetPendingStatusUpdatesAsync(int maxCount = 100, CancellationToken cancellationToken = default);
    Task MarkStatusUpdateAsSyncedAsync(long id, CancellationToken cancellationToken = default);
    Task IncrementStatusUpdateRetryAsync(long id, CancellationToken cancellationToken = default);
    Task StoreLogAsync(Guid correlationId, string workerId, OccurrenceLog log, CancellationToken cancellationToken = default);
    Task<List<StoredLog>> GetPendingLogsAsync(int maxCount = 1000, CancellationToken cancellationToken = default);
    Task MarkLogAsSyncedAsync(long id, CancellationToken cancellationToken = default);
    Task IncrementLogRetryAsync(long id, CancellationToken cancellationToken = default);
    Task RecordJobStartAsync(Guid correlationId, Guid jobId, string jobType, string workerId, CancellationToken cancellationToken = default);
    Task UpdateJobHeartbeatAsync(Guid correlationId, CancellationToken cancellationToken = default);
    Task FinalizeJobAsync(Guid correlationId, JobOccurrenceStatus finalStatus, CancellationToken cancellationToken = default);
    Task<bool> IsJobFinalizedAsync(Guid correlationId, CancellationToken cancellationToken = default);
    Task<LocalStoreStats> GetStatsAsync(CancellationToken cancellationToken = default);
    Task CleanupSyncedRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default);
}

/// <summary>
/// Local SQLite-based storage for job execution state when scheduler/RabbitMQ is unavailable.
/// Implements outbox pattern for reliable message delivery.
/// </summary>
public class LocalStateStore : ILocalStateStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;
    private readonly IMilvaLogger _logger;

    public LocalStateStore(string databasePath, ILoggerFactory loggerFactory)
    {
        var dbFile = Path.Combine(databasePath, $"worker.db");

        // Fixed: Removed 'Journal Mode=WAL' - will be set via PRAGMA instead
        _connectionString = $"Data Source={dbFile};Mode=ReadWriteCreate;Cache=Shared";
        _logger = loggerFactory.CreateMilvaLogger<LocalStateStore>();

        Directory.CreateDirectory(databasePath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
            return;

        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (_initialized)
                return;

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Explicitly enable WAL mode for better concurrent access
            await using (var walCmd = connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                await walCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Set busy timeout (5 seconds)
            await using (var busyCmd = connection.CreateCommand())
            {
                busyCmd.CommandText = "PRAGMA busy_timeout = 5000;";
                await busyCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Set synchronous mode to NORMAL for better performance (still safe with WAL)
            await using (var syncCmd = connection.CreateCommand())
            {
                syncCmd.CommandText = "PRAGMA synchronous=NORMAL;";
                await syncCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            // Create tables...
            await using var cmdStatus = connection.CreateCommand();
            cmdStatus.CommandText = SqlQueries.CreateStatusUpdatesTable;
            await cmdStatus.ExecuteNonQueryAsync(cancellationToken);

            await using var cmdLogs = connection.CreateCommand();
            cmdLogs.CommandText = SqlQueries.CreateLogsTable;
            await cmdLogs.ExecuteNonQueryAsync(cancellationToken);

            await using var cmdMeta = connection.CreateCommand();
            cmdMeta.CommandText = SqlQueries.CreateJobExecutionsTable;
            await cmdMeta.ExecuteNonQueryAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    #region Status Updates

    /// <summary>
    /// Store status update locally (will be synced when connection is available).
    /// </summary>
    public async Task StoreStatusUpdateAsync(Guid correlationId,
                                             Guid jobId,
                                             string workerId,
                                             JobOccurrenceStatus status,
                                             DateTime? startTime = null,
                                             DateTime? endTime = null,
                                             long? durationMs = null,
                                             string result = null,
                                             string exception = null,
                                             CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlQueries.InsertStatusUpdate;

            cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());
            cmd.Parameters.AddWithValue("@JobId", jobId.ToString());
            cmd.Parameters.AddWithValue("@WorkerId", workerId);
            cmd.Parameters.AddWithValue("@Status", (int)status);
            cmd.Parameters.AddWithValue("@StartTime", startTime?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@EndTime", endTime?.ToString("O") ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@DurationMs", durationMs ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Result", result ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Exception", exception ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown in progress - log but don't throw
            _logger?.Information($"StoreStatusUpdate cancelled during shutdown (CorrelationId: {correlationId})");
        }
    }

    /// <summary>
    /// Get pending status updates that need to be synced to scheduler.
    /// Excludes updates from finalized jobs to prevent duplicates.
    /// </summary>
    public async Task<List<StoredStatusUpdate>> GetPendingStatusUpdatesAsync(int maxCount = 100, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.GetNotSyncedStatusUpdates;

        cmd.Parameters.AddWithValue("@MaxCount", maxCount);

        var updates = new List<StoredStatusUpdate>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            updates.Add(new StoredStatusUpdate
            {
                Id = reader.GetInt64(0),
                CorrelationId = Guid.Parse(reader.GetString(1)),
                JobId = Guid.Parse(reader.GetString(2)),
                WorkerId = reader.GetString(3),
                Status = (JobOccurrenceStatus)reader.GetInt32(4),
                StartTime = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5)),
                EndTime = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                DurationMs = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Result = reader.IsDBNull(8) ? null : reader.GetString(8),
                Exception = reader.IsDBNull(9) ? null : reader.GetString(9),
                CreatedAt = DateTime.Parse(reader.GetString(10)),
                RetryCount = reader.GetInt32(11)
            });
        }

        return updates;
    }

    /// <summary>
    /// Mark status update as successfully synced and immediately delete it.
    /// Synced records are deleted immediately to prevent data accumulation.
    /// Only failed sync attempts are retained for retry based on retention period.
    /// </summary>
    public async Task MarkStatusUpdateAsSyncedAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        // Immediately delete synced records instead of just marking them
        cmd.CommandText = @"DELETE FROM StatusUpdates WHERE Id = @Id";

        cmd.Parameters.AddWithValue("@Id", id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Increment retry count for failed sync attempts.
    /// </summary>
    public async Task IncrementStatusUpdateRetryAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.SetStatusUpdateRetry;

        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@LastRetryAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    #endregion

    #region Logs

    /// <summary>
    /// Store log entry locally (will be synced when connection is available).
    /// </summary>
    public async Task StoreLogAsync(Guid correlationId, string workerId, OccurrenceLog log, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = SqlQueries.InsertLog;

            cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());
            cmd.Parameters.AddWithValue("@WorkerId", workerId);
            cmd.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@Level", log.Level);
            cmd.Parameters.AddWithValue("@Message", log.Message);
            cmd.Parameters.AddWithValue("@Category", log.Category ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@ExceptionType", log.ExceptionType ?? (object)DBNull.Value);
            cmd.Parameters.AddWithValue("@Data", log.Data != null ? JsonSerializer.Serialize(log.Data) : DBNull.Value);
            cmd.Parameters.AddWithValue("@CreatedAt", DateTime.UtcNow.ToString("O"));

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown in progress - log but don't throw
            _logger?.Information($"StoreLog cancelled during shutdown (CorrelationId: {correlationId})");
        }
    }

    /// <summary>
    /// Get pending logs that need to be synced to scheduler.
    /// Excludes logs from finalized jobs to prevent duplicates.
    /// </summary>
    public async Task<List<StoredLog>> GetPendingLogsAsync(int maxCount = 1000, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.GetPendingLogs;

        cmd.Parameters.AddWithValue("@MaxCount", maxCount);

        var logs = new List<StoredLog>();

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var dataJson = reader.IsDBNull(8) ? null : reader.GetString(8);

            Dictionary<string, object> data = null;

            if (!string.IsNullOrEmpty(dataJson))
            {
                try
                {
                    data = JsonSerializer.Deserialize<Dictionary<string, object>>(dataJson);
                }
                catch
                {
                    // If deserialization fails, create a simple dictionary with the raw value
                    data = new Dictionary<string, object> { { "raw", dataJson } };
                }
            }

            logs.Add(new StoredLog
            {
                Id = reader.GetInt64(0),
                CorrelationId = Guid.Parse(reader.GetString(1)),
                WorkerId = reader.GetString(2),
                Log = new OccurrenceLog
                {
                    Timestamp = DateTime.Parse(reader.GetString(3)),
                    Level = reader.GetString(4),
                    Message = reader.GetString(5),
                    Category = reader.IsDBNull(6) ? null : reader.GetString(6),
                    ExceptionType = reader.IsDBNull(7) ? null : reader.GetString(7),
                    Data = data
                },
                CreatedAt = DateTime.Parse(reader.GetString(9)),
                RetryCount = reader.GetInt32(10)
            });
        }

        return logs;
    }

    /// <summary>
    /// Mark log as successfully synced and immediately delete it.
    /// Synced records are deleted immediately to prevent data accumulation.
    /// Only failed sync attempts are retained for retry based on retention period.
    /// </summary>
    public async Task MarkLogAsSyncedAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        // Immediately delete synced logs instead of just marking them
        cmd.CommandText = @"DELETE FROM Logs WHERE Id = @Id";

        cmd.Parameters.AddWithValue("@Id", id);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Increment retry count for failed sync attempts.
    /// </summary>
    public async Task IncrementLogRetryAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.SetLogRetry;

        cmd.Parameters.AddWithValue("@Id", id);
        cmd.Parameters.AddWithValue("@LastRetryAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    #endregion

    #region Job Execution Tracking

    /// <summary>
    /// Record job execution start (for recovery scenarios).
    /// </summary>
    public async Task RecordJobStartAsync(Guid correlationId, Guid jobId, string jobType, string workerId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.InsertJobExecution;

        cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());
        cmd.Parameters.AddWithValue("@JobId", jobId.ToString());
        cmd.Parameters.AddWithValue("@JobType", jobType);
        cmd.Parameters.AddWithValue("@WorkerId", workerId);
        cmd.Parameters.AddWithValue("@StartedAt", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@LastHeartbeatAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Update job heartbeat (indicates job is still running).
    /// </summary>
    public async Task UpdateJobHeartbeatAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.UpdateJobHeartbeat;

        cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());
        cmd.Parameters.AddWithValue("@LastHeartbeatAt", DateTime.UtcNow.ToString("O"));

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Mark job as finalized (completed/failed/cancelled).
    /// When a job is finalized, we can safely delete any remaining synced status updates and logs
    /// for that job to prevent accumulation.
    /// </summary>
    public async Task FinalizeJobAsync(Guid correlationId, JobOccurrenceStatus finalStatus, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        // Mark job as finalized
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = SqlQueries.SetJobFinalized;

            cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());
            cmd.Parameters.AddWithValue("@CompletedAt", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@FinalStatus", (int)finalStatus);

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Immediately delete any remaining synced records for this finalized job
        // (Successfully synced records should already be deleted, but this is a safety net)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                DELETE FROM StatusUpdates WHERE CorrelationId = @CorrelationId AND IsSynced = 1;
                DELETE FROM Logs WHERE CorrelationId = @CorrelationId AND IsSynced = 1;
            ";

            cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());

            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Check if a job has been finalized (to prevent redelivery from re-executing).
    /// </summary>
    public async Task<bool> IsJobFinalizedAsync(Guid correlationId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await using var cmd = connection.CreateCommand();

        cmd.CommandText = SqlQueries.IsJobFinalized;

        cmd.Parameters.AddWithValue("@CorrelationId", correlationId.ToString());

        var result = await cmd.ExecuteScalarAsync(cancellationToken);

        return result != null && Convert.ToInt32(result) == 1;
    }

    #endregion

    #region Statistics

    /// <summary>
    /// Get statistics about local store data to monitor accumulation.
    /// </summary>
    public async Task<LocalStoreStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var stats = new LocalStoreStats();

        // Count pending status updates
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM StatusUpdates WHERE IsSynced = 0";
            stats.PendingStatusUpdates = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }

        // Count pending logs
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Logs WHERE IsSynced = 0";
            stats.PendingLogs = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }

        // Count active jobs
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM JobExecutions WHERE IsFinalized = 0";
            stats.ActiveJobs = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }

        // Count finalized jobs
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM JobExecutions WHERE IsFinalized = 1";
            stats.FinalizedJobs = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
        }

        // Get oldest pending record
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT MIN(CreatedAt) FROM (
                    SELECT CreatedAt FROM StatusUpdates WHERE IsSynced = 0
                    UNION ALL
                    SELECT CreatedAt FROM Logs WHERE IsSynced = 0
                )
            ";
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result != null && result != DBNull.Value)
            {
                stats.OldestPendingRecordAge = DateTime.UtcNow - DateTime.Parse(result.ToString());
            }
        }

        return stats;
    }

    #endregion

    #region Cleanup

    /// <summary>
    /// Clean up old records that failed to sync after retention period.
    /// Successfully synced records are already deleted immediately on sync.
    /// This cleanup only removes:
    /// 1. Failed sync attempts (IsSynced = 0) older than retention period
    /// 2. Old finalized job executions
    /// </summary>
    public async Task CleanupSyncedRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default)
    {
        var cutoffTime = DateTime.UtcNow.Subtract(retentionPeriod).ToString("O");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        // Delete old FAILED status updates (couldn't be synced after retention period)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"DELETE FROM StatusUpdates WHERE IsSynced = 0 AND CreatedAt < @CutoffTime";

            cmd.Parameters.AddWithValue("@CutoffTime", cutoffTime);

            var deletedStatus = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (deletedStatus > 0)
                _logger?.Warning($"[LocalStore] Cleaned up {deletedStatus} old FAILED status updates (couldn't sync after {retentionPeriod.TotalDays} days)");
        }

        // Delete old FAILED logs (couldn't be synced after retention period)
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"DELETE FROM Logs WHERE IsSynced = 0 AND CreatedAt < @CutoffTime";

            cmd.Parameters.AddWithValue("@CutoffTime", cutoffTime);

            var deletedLogs = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (deletedLogs > 0)
                _logger?.Warning($"[LocalStore] Cleaned up {deletedLogs} old FAILED logs (couldn't sync after {retentionPeriod.TotalDays} days)");
        }

        // Delete old finalized job executions
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"DELETE FROM JobExecutions WHERE IsFinalized = 1 AND CompletedAt < @CutoffTime";

            cmd.Parameters.AddWithValue("@CutoffTime", cutoffTime);

            var deletedJobs = await cmd.ExecuteNonQueryAsync(cancellationToken);

            if (deletedJobs > 0)
                _logger?.Information($"[LocalStore] Cleaned up {deletedJobs} old finalized job executions");
        }
    }

    #endregion

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            _lock?.Dispose();

            // Final checkpoint before shutdown
            try
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                using var cmd = connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
            catch
            {
                // Ignore errors during disposal
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Final checkpoint before shutdown
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Ignore errors during disposal
        }

        _lock?.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Stored status update record.
/// </summary>
public class StoredStatusUpdate
{
    public long Id { get; set; }
    public Guid CorrelationId { get; set; }
    public Guid JobId { get; set; }
    public string WorkerId { get; set; }
    public JobOccurrenceStatus Status { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public long? DurationMs { get; set; }
    public string Result { get; set; }
    public string Exception { get; set; }
    public DateTime CreatedAt { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Stored log record.
/// </summary>
public class StoredLog
{
    public long Id { get; set; }
    public Guid CorrelationId { get; set; }
    public string WorkerId { get; set; }
    public OccurrenceLog Log { get; set; }
    public DateTime CreatedAt { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Local store statistics for monitoring.
/// </summary>
public class LocalStoreStats
{
    public int PendingStatusUpdates { get; set; }
    public int PendingLogs { get; set; }
    public int ActiveJobs { get; set; }
    public int FinalizedJobs { get; set; }
    public TimeSpan? OldestPendingRecordAge { get; set; }

    public int TotalPendingRecords => PendingStatusUpdates + PendingLogs;
}

public static class SqlQueries
{
    public const string CreateStatusUpdatesTable = @"
        CREATE TABLE IF NOT EXISTS StatusUpdates (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CorrelationId TEXT NOT NULL,
            JobId TEXT NOT NULL,
            WorkerId TEXT NOT NULL,
            Status INTEGER NOT NULL,
            StartTime TEXT,
            EndTime TEXT,
            DurationMs INTEGER,
            Result TEXT,
            Exception TEXT,
            CreatedAt TEXT NOT NULL,
            RetryCount INTEGER DEFAULT 0,
            LastRetryAt TEXT,
            IsSynced INTEGER DEFAULT 0,
            SyncedAt TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_status_synced ON StatusUpdates(IsSynced, CreatedAt);
        CREATE INDEX IF NOT EXISTS idx_status_correlation ON StatusUpdates(CorrelationId);
    ";

    public const string CreateLogsTable = @"
        CREATE TABLE IF NOT EXISTS Logs (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            CorrelationId TEXT NOT NULL,
            WorkerId TEXT NOT NULL,
            Timestamp TEXT NOT NULL,
            Level TEXT NOT NULL,
            Message TEXT NOT NULL,
            Category TEXT,
            ExceptionType TEXT,
            Data TEXT,
            CreatedAt TEXT NOT NULL,
            RetryCount INTEGER DEFAULT 0,
            LastRetryAt TEXT,
            IsSynced INTEGER DEFAULT 0,
            SyncedAt TEXT
        );
        CREATE INDEX IF NOT EXISTS idx_logs_synced ON Logs(IsSynced, CreatedAt);
        CREATE INDEX IF NOT EXISTS idx_logs_correlation ON Logs(CorrelationId);
    ";

    public const string CreateJobExecutionsTable = @"
        CREATE TABLE IF NOT EXISTS JobExecutions (
            CorrelationId TEXT PRIMARY KEY,
            JobId TEXT NOT NULL,
            JobType TEXT NOT NULL,
            WorkerId TEXT NOT NULL,
            StartedAt TEXT NOT NULL,
            LastHeartbeatAt TEXT,
            CompletedAt TEXT,
            FinalStatus INTEGER,
            IsFinalized INTEGER DEFAULT 0
        );
        CREATE INDEX IF NOT EXISTS idx_executions_finalized ON JobExecutions(IsFinalized, StartedAt);
    ";

    public const string InsertStatusUpdate = @"
        INSERT INTO StatusUpdates (
            CorrelationId, JobId, WorkerId, Status, StartTime, EndTime, 
            DurationMs, Result, Exception, CreatedAt
        ) VALUES (
            @CorrelationId, @JobId, @WorkerId, @Status, @StartTime, @EndTime,
            @DurationMs, @Result, @Exception, @CreatedAt
        );
    ";

    public const string GetNotSyncedStatusUpdates = @"
            SELECT 
                s.Id, s.CorrelationId, s.JobId, s.WorkerId, s.Status, s.StartTime, s.EndTime,
                s.DurationMs, s.Result, s.Exception, s.CreatedAt, s.RetryCount
            FROM StatusUpdates s
            LEFT JOIN JobExecutions j ON s.CorrelationId = j.CorrelationId
            WHERE s.IsSynced = 0
            AND (j.IsFinalized IS NULL OR j.IsFinalized = 0)
            ORDER BY s.CreatedAt ASC
            LIMIT @MaxCount
    ";

    public const string SetStatusUpdateRetry = @"
            UPDATE StatusUpdates
            SET RetryCount = RetryCount + 1, LastRetryAt = @LastRetryAt
            WHERE Id = @Id
    ";

    public const string InsertLog = @"
            INSERT INTO Logs (
                CorrelationId, WorkerId, Timestamp, Level, Message, 
                Category, ExceptionType, Data, CreatedAt
            ) VALUES (
                @CorrelationId, @WorkerId, @Timestamp, @Level, @Message,
                @Category, @ExceptionType, @Data, @CreatedAt
            )
    ";

    public const string GetPendingLogs = @"
            SELECT 
                l.Id, l.CorrelationId, l.WorkerId, l.Timestamp, l.Level, l.Message, 
                l.Category, l.ExceptionType, l.Data, l.CreatedAt, l.RetryCount
            FROM Logs l
            LEFT JOIN JobExecutions j ON l.CorrelationId = j.CorrelationId
            WHERE l.IsSynced = 0 
            AND (j.IsFinalized IS NULL OR j.IsFinalized = 0)
            ORDER BY l.CreatedAt ASC
            LIMIT @MaxCount
    ";

    public const string SetLogRetry = @"
            UPDATE Logs
            SET RetryCount = RetryCount + 1, LastRetryAt = @LastRetryAt
            WHERE Id = @Id
    ";

    public const string InsertJobExecution = @"
            INSERT OR REPLACE INTO JobExecutions (
                CorrelationId, JobId, JobType, WorkerId, StartedAt, LastHeartbeatAt
            ) VALUES (
                @CorrelationId, @JobId, @JobType, @WorkerId, @StartedAt, @LastHeartbeatAt
            )
    ";

    public const string UpdateJobHeartbeat = @"
            UPDATE JobExecutions
            SET LastHeartbeatAt = @LastHeartbeatAt
            WHERE CorrelationId = @CorrelationId
    ";

    public const string SetJobFinalized = @"
            UPDATE JobExecutions
            SET CompletedAt = @CompletedAt, FinalStatus = @FinalStatus, IsFinalized = 1
            WHERE CorrelationId = @CorrelationId
    ";

    public const string IsJobFinalized = @"
            SELECT IsFinalized 
            FROM JobExecutions 
            WHERE CorrelationId = @CorrelationId 
            AND IsFinalized = 1
            LIMIT 1
    ";
}