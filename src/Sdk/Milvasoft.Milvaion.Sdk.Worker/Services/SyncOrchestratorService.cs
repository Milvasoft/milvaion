using Microsoft.Extensions.Hosting;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;

namespace Milvasoft.Milvaion.Sdk.Worker.Services;

/// <summary>
/// Background service that periodically synchronizes local state to scheduler.
/// Handles retry logic with exponential backoff for failed sync attempts.
/// </summary>
public class SyncOrchestratorService(OutboxService outboxService,
                                     LocalStateStore localStore,
                                     ConnectionMonitor connectionMonitor,
                                     IMilvaLogger logger,
                                     Options.WorkerOptions options) : BackgroundService
{
    private readonly OutboxService _outboxService = outboxService;
    private readonly LocalStateStore _localStore = localStore;
    private readonly ConnectionMonitor _connectionMonitor = connectionMonitor;
    private readonly IMilvaLogger _logger = logger;
    private readonly TimeSpan _syncInterval = TimeSpan.FromSeconds(options.OfflineResilience?.SyncIntervalSeconds ?? 30);
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(options.OfflineResilience?.CleanupIntervalHours ?? 1);
    private readonly TimeSpan _recordRetention = TimeSpan.FromDays(options.OfflineResilience?.RecordRetentionDays ?? 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.Information("Sync Orchestrator started. SyncInterval: {SyncInterval}s, CleanupInterval: {CleanupInterval}h, RecordRetention: {RecordRetention}d", _syncInterval.TotalSeconds, _cleanupInterval.TotalHours, _recordRetention.TotalDays);

        // Initialize local state store
        await _localStore.InitializeAsync(stoppingToken);

        var cleanupTimer = DateTime.UtcNow;
        var statsTimer = DateTime.UtcNow;
        var statsInterval = TimeSpan.FromMinutes(5); // Log stats every 5 minutes

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check connection health
                var isHealthy = _connectionMonitor.IsRabbitMQHealthy;

                if (isHealthy)
                {
                    // Sync status updates
                    var statusResult = await _outboxService.SyncStatusUpdatesAsync(maxBatchSize: 100, maxRetries: 3, cancellationToken: stoppingToken);

                    if (!statusResult.Skipped)
                        _logger?.Information("Status sync: {Message} (Synced: {Synced}, Failed: {Failed})", statusResult.Message, statusResult.SyncedCount, statusResult.FailedCount);

                    // Sync logs
                    var logsResult = await _outboxService.SyncLogsAsync(maxBatchSize: 1000, maxRetries: 3, cancellationToken: stoppingToken);

                    if (!logsResult.Skipped)
                        _logger?.Information("Logs sync: {Message} (Synced: {Synced}, Failed: {Failed})", logsResult.Message, logsResult.SyncedCount, logsResult.FailedCount);
                }
                else
                    _logger?.Information("Connection unhealthy, skipping sync cycle");

                // Periodic statistics logging
                if (DateTime.UtcNow - statsTimer >= statsInterval)
                {
                    var stats = await _localStore.GetStatsAsync(stoppingToken);

                    if (stats.TotalPendingRecords > 0)
                    {
                        var oldestAge = stats.OldestPendingRecordAge?.ToString(@"dd\.hh\:mm\:ss") ?? "N/A";

                        _logger?.Debug("[LocalStore Stats] Pending: {PendingTotal} (Status: {PendingStatus}, Logs: {PendingLogs}), Active Jobs: {ActiveJobs}, Oldest Record: {OldestAge}", stats.TotalPendingRecords, stats.PendingStatusUpdates, stats.PendingLogs, stats.ActiveJobs, oldestAge);

                        if (stats.OldestPendingRecordAge?.TotalHours > 1)
                            _logger?.Warning("[LocalStore] Old pending records detected! Oldest: {OldestAge}. Check connection health.", oldestAge);
                    }

                    statsTimer = DateTime.UtcNow;
                }

                // Periodic cleanup of old records
                if (DateTime.UtcNow - cleanupTimer >= _cleanupInterval)
                {
                    _logger?.Information("Running cleanup of old failed sync attempts (retention: {RecordRetention} days)...", _recordRetention.TotalDays);

                    await _localStore.CleanupSyncedRecordsAsync(_recordRetention, stoppingToken);

                    cleanupTimer = DateTime.UtcNow;
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Error in sync orchestrator cycle");
            }

            // Wait for next sync interval
            try
            {
                await Task.Delay(_syncInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                break;
            }
        }

        _logger?.Information("Sync Orchestrator stopping...");
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.Information("Sync Orchestrator: Performing final sync before shutdown...");

        try
        {
            // Attempt final sync
            await _outboxService.SyncStatusUpdatesAsync(maxBatchSize: 100, maxRetries: 1, cancellationToken: cancellationToken);
            await _outboxService.SyncLogsAsync(maxBatchSize: 1000, maxRetries: 1, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Error during final sync on shutdown");
        }

        await base.StopAsync(cancellationToken);
    }
}
