using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

namespace Milvasoft.Milvaion.Sdk.Worker.Persistence;

/// <summary>
/// Outbox service that stores status updates and logs locally first,
/// then synchronizes with scheduler when connection is available.
/// Ensures no data loss even if RabbitMQ/Redis is temporarily unavailable.
/// </summary>
public class OutboxService(ILocalStateStore localStore,
                           IStatusUpdatePublisher statusPublisher,
                           ILogPublisher logPublisher,
                           IConnectionMonitor connectionMonitor,
                           IMilvaLogger logger)
{
    private readonly ILocalStateStore _localStore = localStore;
    private readonly IMilvaLogger _logger = logger;
    private readonly IStatusUpdatePublisher _statusPublisher = statusPublisher;
    private readonly ILogPublisher _logPublisher = logPublisher;
    private readonly IConnectionMonitor _connectionMonitor = connectionMonitor;

    /// <summary>
    /// Gets the local state store (for direct access to job tracking).
    /// </summary>
    public ILocalStateStore GetLocalStore() => _localStore;

    #region Status Updates

    /// <summary>
    /// Publish status update with resilience:
    /// 1. Try to publish to RabbitMQ first
    /// 2. If fails, store locally for later sync
    /// </summary>
    public async Task PublishStatusUpdateAsync(Guid correlationId,
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
        // STEP 1: Try to publish immediately if connection is healthy
        if (_connectionMonitor.IsRabbitMQHealthy)
        {
            try
            {
                await _statusPublisher.PublishStatusAsync(correlationId,
                                                          jobId,
                                                          workerId,
                                                          status,
                                                          startTime,
                                                          endTime,
                                                          durationMs,
                                                          result,
                                                          exception,
                                                          cancellationToken);

                _logger?.Debug("Status update published successfully for CorrelationId {CorrelationId}, Status: {Status}", correlationId, status);

                // SUCCESS - Don't store locally, direct publish succeeded
                return;
            }
            catch (Exception ex)
            {
                // Log warning and continue to local storage
                _logger?.Warning(ex, "Failed to publish status update for CorrelationId {CorrelationId}. Will store locally for retry.", correlationId);
            }
        }

        // STEP 2: Store locally only if direct publish failed or connection unhealthy
        await _localStore.StoreStatusUpdateAsync(correlationId,
                                                 jobId,
                                                 workerId,
                                                 status,
                                                 startTime,
                                                 endTime,
                                                 durationMs,
                                                 result,
                                                 exception,
                                                 cancellationToken);

        _logger?.Warning("Status update stored locally for CorrelationId {CorrelationId} (RabbitMQ: {Status}). Will sync later.", correlationId, _connectionMonitor.IsRabbitMQHealthy ? "failed" : "unhealthy");
    }

    #endregion

    #region Logs

    /// <summary>
    /// Publish log with resilience:
    /// 1. Try to publish to RabbitMQ first
    /// 2. If fails, store locally for later sync
    /// </summary>
    public async Task PublishLogAsync(Guid correlationId,
                                      string workerId,
                                      OccurrenceLog log,
                                      CancellationToken cancellationToken = default)
    {
        // STEP 1: Try to publish immediately if connection is healthy
        if (_connectionMonitor.IsRabbitMQHealthy)
        {
            try
            {
                await _logPublisher.PublishLogAsync(correlationId, workerId, log, cancellationToken);

                _logger?.Debug("Log published successfully for CorrelationId {CorrelationId}, Level: {Level}", correlationId, log.Level);

                // SUCCESS - Don't store locally, direct publish succeeded
                return;
            }
            catch (Exception ex)
            {
                // Log warning and continue to local storage
                _logger?.Debug(ex, "Failed to publish log for CorrelationId {CorrelationId}. Will store locally for retry.", correlationId);
            }
        }

        // STEP 2: Store locally only if direct publish failed or connection unhealthy
        await _localStore.StoreLogAsync(correlationId, workerId, log, cancellationToken);

        _logger?.Debug("Log stored locally for CorrelationId {CorrelationId} (RabbitMQ: {Status}). Will sync later.", correlationId, _connectionMonitor.IsRabbitMQHealthy ? "failed" : "unhealthy");
    }

    #endregion

    #region Heartbeat

    /// <summary>
    /// Publishes a heartbeat for a running job to prevent zombie detection.
    /// Updates LastHeartbeat field in JobOccurrence via StatusTracker.
    /// </summary>
    public async Task PublishJobHeartbeatAsync(Guid correlationId,
                                               Guid jobId,
                                               string workerId,
                                               CancellationToken cancellationToken = default)
    {
        // Only attempt if connection is healthy - heartbeat is not critical enough to store locally
        if (!_connectionMonitor.IsRabbitMQHealthy)
        {
            _logger?.Debug("Skipping job heartbeat for CorrelationId {CorrelationId} - RabbitMQ unhealthy", correlationId);
            return;
        }

        try
        {
            // Publish a status update with Running status to refresh LastHeartbeat
            await _statusPublisher.PublishStatusAsync(correlationId,
                                                      jobId,
                                                      workerId,
                                                      JobOccurrenceStatus.Running,
                                                      cancellationToken: cancellationToken);

            _logger?.Debug("Job heartbeat published for CorrelationId {CorrelationId}", correlationId);
        }
        catch (Exception ex)
        {
            // Non-critical - just log and continue
            _logger?.Debug(ex, "Failed to publish job heartbeat for CorrelationId {CorrelationId}", correlationId);
        }
    }

    #endregion

    #region Synchronization

    /// <summary>
    /// Synchronize pending status updates to scheduler.
    /// Called periodically by sync orchestrator.
    /// </summary>
    public async Task<SyncResult> SyncStatusUpdatesAsync(int maxBatchSize = 100, int maxRetries = 3, CancellationToken cancellationToken = default)
    {
        var result = new SyncResult();

        if (!_connectionMonitor.IsRabbitMQHealthy)
        {
            result.Skipped = true;
            result.Message = "RabbitMQ unhealthy, skipping sync";
            return result;
        }

        try
        {
            var pendingUpdates = await _localStore.GetPendingStatusUpdatesAsync(maxBatchSize, cancellationToken);

            if (pendingUpdates.Count == 0)
            {
                result.Success = true;
                result.Message = "No pending status updates";
                return result;
            }

            _logger?.Debug("Syncing {Count} pending status updates...", pendingUpdates.Count);

            foreach (var update in pendingUpdates)
            {
                // Skip if exceeded max retries
                if (update.RetryCount >= maxRetries)
                {
                    _logger?.Debug("Status update {Id} exceeded max retries ({RetryCount}). Marking as synced to prevent blocking.", update.Id, update.RetryCount);

                    await _localStore.MarkStatusUpdateAsSyncedAsync(update.Id, cancellationToken);

                    result.FailedCount++;

                    continue;
                }

                try
                {
                    // Publish to RabbitMQ
                    await _statusPublisher.PublishStatusAsync(update.CorrelationId,
                                                              update.JobId,
                                                              update.WorkerId,
                                                              update.Status,
                                                              update.StartTime,
                                                              update.EndTime,
                                                              update.DurationMs,
                                                              update.Result,
                                                              update.Exception,
                                                              cancellationToken);

                    // Mark as synced
                    await _localStore.MarkStatusUpdateAsSyncedAsync(update.Id, cancellationToken);

                    result.SyncedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Warning(ex, "Failed to sync status update {Id} for CorrelationId {CorrelationId}. Will retry.", update.Id, update.CorrelationId);

                    // Increment retry count
                    await _localStore.IncrementStatusUpdateRetryAsync(update.Id, cancellationToken);

                    result.FailedCount++;
                }
            }

            result.Success = result.SyncedCount > 0 || result.FailedCount == 0;
            result.Message = $"Synced {result.SyncedCount}/{pendingUpdates.Count} status updates";
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Error during status update sync");

            result.Success = false;
            result.Message = $"Sync error: {ex.Message}";
        }

        return result;
    }

    /// <summary>
    /// Synchronize pending logs to scheduler.
    /// Called periodically by sync orchestrator.
    /// </summary>
    public async Task<SyncResult> SyncLogsAsync(int maxBatchSize = 1000, int maxRetries = 3, CancellationToken cancellationToken = default)
    {
        var result = new SyncResult();

        if (!_connectionMonitor.IsRabbitMQHealthy)
        {
            result.Skipped = true;
            result.Message = "RabbitMQ unhealthy, skipping sync";

            return result;
        }

        try
        {
            var pendingLogs = await _localStore.GetPendingLogsAsync(maxBatchSize, cancellationToken);

            if (pendingLogs.Count == 0)
            {
                result.Success = true;
                result.Message = "No pending logs";
                return result;
            }

            _logger?.Debug("Syncing {Count} pending logs...", pendingLogs.Count);

            foreach (var storedLog in pendingLogs)
            {
                // Skip if exceeded max retries
                if (storedLog.RetryCount >= maxRetries)
                {
                    _logger?.Debug("Log {Id} exceeded max retries ({RetryCount}). Marking as synced.", storedLog.Id, storedLog.RetryCount);

                    await _localStore.MarkLogAsSyncedAsync(storedLog.Id, cancellationToken);

                    result.FailedCount++;

                    continue;
                }

                try
                {
                    // Publish to RabbitMQ
                    await _logPublisher.PublishLogAsync(storedLog.CorrelationId, storedLog.WorkerId, storedLog.Log, cancellationToken);

                    // Mark as synced
                    await _localStore.MarkLogAsSyncedAsync(storedLog.Id, cancellationToken);

                    result.SyncedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to sync log {Id} for CorrelationId {CorrelationId}. Will retry.", storedLog.Id, storedLog.CorrelationId);

                    // Increment retry count
                    await _localStore.IncrementLogRetryAsync(storedLog.Id, cancellationToken);

                    result.FailedCount++;
                }
            }

            result.Success = result.SyncedCount > 0 || result.FailedCount == 0;
            result.Message = $"Synced {result.SyncedCount}/{pendingLogs.Count} logs";
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Error during log sync");

            result.Success = false;
            result.Message = $"Sync error: {ex.Message}";
        }

        return result;
    }

    #endregion
}

/// <summary>
/// Result of a synchronization operation.
/// </summary>
public class SyncResult
{
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public int SyncedCount { get; set; }
    public int FailedCount { get; set; }
    public string Message { get; set; }
}
