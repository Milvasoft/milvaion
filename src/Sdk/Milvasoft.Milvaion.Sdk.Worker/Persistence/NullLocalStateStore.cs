using Milvasoft.Milvaion.Sdk.Utils;

namespace Milvasoft.Milvaion.Sdk.Worker.Persistence;

/// <summary>
/// Local state store used when <c>OfflineResilience.Enabled</c> is false. Nothing is persisted, so
/// <see cref="OutboxService"/> publishes directly and a message that cannot be published is lost.
/// </summary>
internal sealed class NullLocalStateStore : ILocalStateStore
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreStatusUpdateAsync(Guid occurrenceId, Guid jobId, string workerId, string instanceId, JobOccurrenceStatus status, DateTime? startTime = null, DateTime? endTime = null, long? durationMs = null, string result = null, string exception = null, DateTime? retryAfter = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<List<StoredStatusUpdate>> GetPendingStatusUpdatesAsync(int maxCount = 100, CancellationToken cancellationToken = default) => Task.FromResult(new List<StoredStatusUpdate>());

    public Task MarkStatusUpdateAsSyncedAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task IncrementStatusUpdateRetryAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StoreLogAsync(Guid occurrenceId, string workerId, OccurrenceLog log, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<List<StoredLog>> GetPendingLogsAsync(int maxCount = 1000, CancellationToken cancellationToken = default) => Task.FromResult(new List<StoredLog>());

    public Task MarkLogAsSyncedAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task IncrementLogRetryAsync(long id, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RecordJobStartAsync(Guid occurrenceId, Guid jobId, string jobType, string workerId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateJobHeartbeatAsync(Guid occurrenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task FinalizeJobAsync(Guid occurrenceId, JobOccurrenceStatus finalStatus, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> IsJobFinalizedAsync(Guid occurrenceId, CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<LocalStoreStats> GetStatsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LocalStoreStats());

    public Task CleanupSyncedRecordsAsync(TimeSpan retentionPeriod, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
