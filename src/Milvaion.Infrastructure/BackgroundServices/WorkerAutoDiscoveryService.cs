using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes worker registration and heartbeat messages from RabbitMQ.
/// Stores runtime state in Redis for high performance.
/// </summary>
public class WorkerAutoDiscoveryService(IRedisWorkerService redisWorkerService,
                                        RabbitMQConnectionFactory rabbitMQFactory,
                                        IOptions<WorkerAutoDiscoveryOptions> options,
                                        IAlertNotifier alertNotifier,
                                        ILoggerFactory loggerFactory,
                                        IServiceProvider serviceProvider,
                                        BackgroundServiceMetrics metrics,
                                        IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IRedisWorkerService _redisWorkerService = redisWorkerService;
    private readonly RabbitMQConnectionFactory _rabbitMQFactory = rabbitMQFactory;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<WorkerAutoDiscoveryService>();
    private readonly WorkerAutoDiscoveryOptions _options = options.Value;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private IChannel _registrationChannel;
    private IChannel _heartbeatChannel;

    // Heartbeat batch processing with per-instance deduplication
    private readonly SemaphoreSlim _batchLock = new(1, 1);

    // Channel thread-safety: IChannel is NOT thread-safe, serialize ACK/NACK operations
    private readonly SemaphoreSlim _heartbeatChannelLock = new(1, 1);
    private readonly SemaphoreSlim _registrationChannelLock = new(1, 1);

    // Per-instance deduplication: only keep latest heartbeat per instance
    // This dramatically reduces Redis load when workers send many heartbeats rapidly
    private readonly ConcurrentDictionary<string, (WorkerHeartbeatMessage Heartbeat, ulong DeliveryTag, DateTime ReceivedAt)> _latestHeartbeats = new();

    /// <inheritdoc/>
    protected override string ServiceName => "WorkerAutoDiscovery";

    /// <summary>
    /// Executes the background service to listen for worker messages.
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Worker auto discovery is disabled. Skipping startup.");

            return;
        }

        _logger.Information("Worker auto discovery is starting (Redis-based)...");

        // Start batch processor for heartbeats
        _ = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(100, stoppingToken); // Process every 100ms
                await ProcessHeartbeatBatchAsync(stoppingToken);
                TrackMemoryAfterIteration();
            }
        }, stoppingToken);

        try
        {
            await ConnectAndConsumeAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Worker auto discovery is shutting down");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Fatal error in worker auto discovery");
            throw;
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Get channels from shared connection factory
        _registrationChannel = await _rabbitMQFactory.CreateChannelAsync(stoppingToken);
        _heartbeatChannel = await _rabbitMQFactory.CreateChannelAsync(stoppingToken);

        // Track channel health — if either channel dies, break out of Delay(Infinite) to trigger retry
        var channelDiedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        _registrationChannel.ChannelShutdownAsync += async (sender, args) =>
        {
            _logger.Warning("WorkerAutoDiscovery registration channel shutdown. Reason: {Reason}", args.ReplyText);
            await channelDiedCts.CancelAsync();
        };

        _heartbeatChannel.ChannelShutdownAsync += async (sender, args) =>
        {
            _logger.Warning("WorkerAutoDiscovery heartbeat channel shutdown. Reason: {Reason}", args.ReplyText);
            await channelDiedCts.CancelAsync();
        };

        // Declare queues
        await _registrationChannel.QueueDeclareAsync(WorkerConstant.Queues.WorkerRegistration, true, false, false, null, cancellationToken: stoppingToken);
        await _heartbeatChannel.QueueDeclareAsync(WorkerConstant.Queues.WorkerHeartbeat, true, false, false, null, cancellationToken: stoppingToken);

        // Set QoS prefetch to limit in-flight messages and prevent queue buildup during Redis slowdowns
        await _registrationChannel.BasicQosAsync(0, 10, false, stoppingToken);
        await _heartbeatChannel.BasicQosAsync(0, 500, false, stoppingToken); // High prefetch for high-frequency heartbeats

        _logger.Information("WorkerAutoDiscovery connected to RabbitMQ (shared connection). RegistrationChannel: {RegCh}, HeartbeatChannel: {HbCh}", _registrationChannel.ChannelNumber, _heartbeatChannel.ChannelNumber);

        // Registration consumer
        var registrationConsumer = new AsyncEventingBasicConsumer(_registrationChannel);

        registrationConsumer.ReceivedAsync += async (model, ea) =>
        {
            await ProcessRegistrationAsync(ea, stoppingToken);

            TrackMemoryAfterIteration();
        };

        await _registrationChannel.BasicConsumeAsync(WorkerConstant.Queues.WorkerRegistration, false, registrationConsumer, stoppingToken);

        // Heartbeat consumer
        var heartbeatConsumer = new AsyncEventingBasicConsumer(_heartbeatChannel);

        heartbeatConsumer.ReceivedAsync += async (model, ea) =>
        {
            await ProcessHeartbeatAsync(ea, stoppingToken);

            TrackMemoryAfterIteration();
        };

        await _heartbeatChannel.BasicConsumeAsync(WorkerConstant.Queues.WorkerHeartbeat, false, heartbeatConsumer, stoppingToken);

        // Start zombie worker cleanup task (detect dead workers and cleanup their consumer counts)
        _ = Task.Run(async () => await CleanupZombieWorkersAsync(stoppingToken), stoppingToken);

        // Keep running until cancellation OR channel death
        try
        {
            await Task.Delay(Timeout.Infinite, channelDiedCts.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Channel died, not graceful shutdown — throw to trigger retry loop
            _logger.Warning("WorkerAutoDiscovery channel died. Will reconnect...");
            throw new InvalidOperationException("RabbitMQ channel closed unexpectedly. Reconnecting...");
        }

        // Cleanup channels before retry
        await _registrationChannel.SafeCloseAsync(_logger, default);
        await _heartbeatChannel.SafeCloseAsync(_logger, default);
    }

    private async Task ProcessRegistrationAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            // Skip processing if shutdown is requested
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("Skipping registration processing due to shutdown");
                return;
            }

            var registration = JsonSerializer.Deserialize<WorkerDiscoveryRequest>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (registration == null)
            {
                _logger.Debug("Failed to deserialize worker registration");

                await _registrationChannel.SafeNackAsync(ea.DeliveryTag, _registrationChannelLock, _logger, cancellationToken);

                return;
            }

            // Store in Redis (fast, in-memory)
            var success = await _redisWorkerService.RegisterWorkerAsync(registration, cancellationToken);

            if (success)
            {
                var existingWorker = await _redisWorkerService.GetWorkerAsync(registration.WorkerId, cancellationToken);
                var instanceCount = existingWorker?.Instances?.Count ?? 1;

                _metrics.RecordWorkerRegistration(registration.WorkerId);

                _logger.Information("Worker {WorkerId} (Instance: {InstanceId}) registered in Redis. Total instances: {Count}", registration.WorkerId, registration.InstanceId, instanceCount);

                // Fire-and-forget: sync RoutingPatterns in the DB for this worker's jobs
                // (and reassign jobs stranded on now-inactive workers).
                _ = Task.Run(() => SyncRoutingPatternsAsync(registration, CancellationToken.None), CancellationToken.None);
            }
            else
            {
                _logger.Error("Failed to register worker {WorkerId} in Redis", registration.WorkerId);
            }

            await _registrationChannel.SafeAckAsync(ea.DeliveryTag, _registrationChannelLock, _logger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process worker registration");

            await _registrationChannel.SafeNackAsync(ea.DeliveryTag, _registrationChannelLock, _logger, cancellationToken);
        }
    }

    /// <summary>
    /// Synchronizes the <see cref="Milvasoft.Milvaion.Sdk.Domain.ScheduledJob.RoutingPattern"/> of a worker's jobs
    /// in the database whenever the worker (re)registers. If a job type is currently assigned to a different worker
    /// that is no longer active (no Redis instances), the job is reassigned to the registering worker so it can be
    /// dispatched again instead of publishing to an unroutable (offline) worker.
    /// </summary>
    private async Task SyncRoutingPatternsAsync(WorkerDiscoveryRequest registration, CancellationToken cancellationToken)
    {
        try
        {
            if (registration.RoutingPatterns == null || registration.RoutingPatterns.Count == 0)
                return;

            await using var scope = _serviceProvider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

            foreach (var (jobTypeName, newPattern) in registration.RoutingPatterns)
            {
                try
                {
                    // Case 1: jobs already owned by this worker — just refresh the routing pattern.
                    var updatedCount = await dbContext.ScheduledJobs
                        .Where(j => j.JobNameInWorker == jobTypeName && j.WorkerId == registration.WorkerId)
                        .ExecuteUpdateAsync(s => s.SetProperty(j => j.RoutingPattern, newPattern), cancellationToken);

                    if (updatedCount > 0)
                    {
                        _logger.Information("Updated RoutingPattern for {Count} '{JobType}' jobs (WorkerId={WorkerId}): {Pattern}", updatedCount, jobTypeName, registration.WorkerId, newPattern);
                        continue;
                    }

                    // Case 2: no jobs owned by this worker — check whether this job type is stranded
                    // on a different worker.
                    var staleJob = await dbContext.ScheduledJobs
                        .Where(j => j.JobNameInWorker == jobTypeName && j.WorkerId != registration.WorkerId)
                        .Select(j => new { j.Id, j.WorkerId })
                        .FirstOrDefaultAsync(cancellationToken);

                    if (staleJob == null)
                        continue;

                    var oldWorker = await _redisWorkerService.GetWorkerAsync(staleJob.WorkerId, cancellationToken);
                    var isOldWorkerActive = oldWorker?.Instances?.Count > 0;

                    // Only reassign if the previous owner is genuinely gone.
                    if (!isOldWorkerActive)
                    {
                        var reassigned = await dbContext.ScheduledJobs
                            .Where(j => j.JobNameInWorker == jobTypeName && j.WorkerId == staleJob.WorkerId)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(j => j.WorkerId, registration.WorkerId)
                                .SetProperty(j => j.RoutingPattern, newPattern), cancellationToken);

                        if (reassigned > 0)
                            _logger.Information("Reassigned {Count} stale '{JobType}' jobs from inactive worker '{OldWorker}' to '{NewWorker}' with pattern '{Pattern}'", reassigned, jobTypeName, staleJob.WorkerId, registration.WorkerId, newPattern);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to sync routing pattern for job type '{JobType}'", jobTypeName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SyncRoutingPatternsAsync failed for worker '{WorkerId}'", registration.WorkerId);
        }
    }

    private async Task ProcessHeartbeatAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.Debug("Skipping heartbeat processing due to shutdown");
                return;
            }

            var heartbeat = JsonSerializer.Deserialize<WorkerHeartbeatMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (heartbeat == null)
            {
                await _heartbeatChannel.SafeNackAsync(ea.DeliveryTag, _heartbeatChannelLock, _logger, cancellationToken);
                return;
            }

            // Graceful shutdown - immediate processing
            if (heartbeat.IsStopping)
            {
                _logger.Warning("Worker instance {InstanceId} is shutting down gracefully, performing immediate cleanup", heartbeat.InstanceId);

                await using var scope = _serviceProvider.CreateAsyncScope();
                var redisScheduler = scope.ServiceProvider.GetService<IRedisSchedulerService>();

                if (redisScheduler != null)
                {
                    try
                    {
                        var removed = await redisScheduler.RemoveAllRunningJobsForWorkerAsync(heartbeat.InstanceId, cancellationToken);
                        _logger.Information("Cleaned up {Count} running jobs for shutting down instance {InstanceId}", removed, heartbeat.InstanceId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Failed to cleanup running jobs for {InstanceId}", heartbeat.InstanceId);
                    }
                }

                var success = await _redisWorkerService.RemoveWorkerInstanceAsync(heartbeat.WorkerId, heartbeat.InstanceId, cancellationToken);

                if (success)
                    _logger.Information("Worker instance {InstanceId} cleaned up successfully on graceful shutdown", heartbeat.InstanceId);

                await _heartbeatChannel.SafeAckAsync(ea.DeliveryTag, _heartbeatChannelLock, _logger, cancellationToken);
                return;
            }

            // Per-instance deduplication: only keep the latest heartbeat per instance
            // This prevents queue flooding when workers send many heartbeats rapidly
            var instanceKey = heartbeat.InstanceId;
            var now = DateTime.UtcNow;

            // Try to update existing entry or add new one
            _latestHeartbeats.AddOrUpdate(
                instanceKey,
                // Add new entry
                (heartbeat, ea.DeliveryTag, now),
                // Update existing: keep newer heartbeat, ACK older delivery tag immediately
                (key, existing) =>
                {
                    // ACK the older message immediately (fire-and-forget, non-blocking)
                    if (heartbeat.Timestamp >= existing.Heartbeat.Timestamp)
                    {
                        // New heartbeat is newer - ACK the old one
                        _ = _heartbeatChannel.SafeAckAsync(existing.DeliveryTag, _heartbeatChannelLock, _logger, cancellationToken);
                        return (heartbeat, ea.DeliveryTag, now);
                    }
                    else
                    {
                        // Existing heartbeat is newer - ACK the incoming one
                        _ = _heartbeatChannel.SafeAckAsync(ea.DeliveryTag, _heartbeatChannelLock, _logger, cancellationToken);
                        return existing;
                    }
                });

            _logger.Debug("Heartbeat stored for batch processing: {WorkerId}/{InstanceId} (Unique instances: {Count})", heartbeat.WorkerId, heartbeat.InstanceId, _latestHeartbeats.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process worker heartbeat");
            await _heartbeatChannel.SafeNackAsync(ea.DeliveryTag, _heartbeatChannelLock, _logger, cancellationToken);
        }
    }

    /// <summary>
    /// Process heartbeats in batch from the deduplication dictionary.
    /// Only the latest heartbeat per instance is processed, reducing Redis load significantly.
    /// </summary>
    private async Task ProcessHeartbeatBatchAsync(CancellationToken cancellationToken)
    {
        // Wait up to 50ms for lock (don't skip immediately, but don't block too long either)
        if (!await _batchLock.WaitAsync(50, cancellationToken))
            return;

        var sw = Stopwatch.StartNew();

        try
        {
            if (_latestHeartbeats.IsEmpty)
                return;

            // Snapshot and clear the dictionary atomically
            var snapshot = new List<(string InstanceId, WorkerHeartbeatMessage Heartbeat, ulong DeliveryTag)>();

            foreach (var kvp in _latestHeartbeats)
            {
                if (_latestHeartbeats.TryRemove(kvp.Key, out var entry))
                {
                    snapshot.Add((kvp.Key, entry.Heartbeat, entry.DeliveryTag));
                }
            }

            if (snapshot.Count == 0)
                return;

            // Build batch for Redis (deduplicated - one per instance)
            var batch = snapshot.Select(s => (s.Heartbeat.WorkerId, s.Heartbeat.InstanceId, s.Heartbeat.CurrentJobs, s.Heartbeat.MemoryBytes, s.Heartbeat.CpuUsagePercent, s.Heartbeat.Timestamp)).ToList();

            var successCount = await _redisWorkerService.BulkUpdateHeartbeatsAsync(batch, cancellationToken);

            // ACK each message individually (can't use bulk ACK with deduplication since delivery tags aren't sequential)
            foreach (var (InstanceId, Heartbeat, DeliveryTag) in snapshot)
                await _heartbeatChannel.SafeAckAsync(DeliveryTag, _heartbeatChannelLock, _logger, cancellationToken);

            // Record metrics
            _metrics.RecordWorkerHeartbeats(successCount);
            _metrics.RecordHeartbeatProcessDuration(sw.Elapsed.TotalMilliseconds, batch.Count);
            _metrics.SetActiveWorkersCount(snapshot.Select(s => s.Heartbeat.WorkerId).Distinct().Count());

            _logger.Debug("Processed {SuccessCount}/{TotalCount} heartbeats in batch (deduplicated from {InstanceCount} instances)", successCount, batch.Count, snapshot.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process heartbeat batch");
        }
        finally
        {
            _batchLock.Release();
        }
    }

    /// <summary>
    /// Stops the background service and cleans up resources.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("Worker auto discovery is stopping...");

        // Process remaining heartbeats before shutdown
        try
        {
            await ProcessHeartbeatBatchAsync(CancellationToken.None);
            _logger.Information("Processed remaining {Count} heartbeats during shutdown", _latestHeartbeats.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to process remaining heartbeats during shutdown");
        }

        // Dispose semaphores
        _batchLock?.Dispose();
        _heartbeatChannelLock?.Dispose();
        _registrationChannelLock?.Dispose();

        // Close only channels (connection is managed by RabbitMQConnectionFactory)
        await _registrationChannel.SafeCloseAsync(_logger, cancellationToken);
        await _heartbeatChannel.SafeCloseAsync(_logger, cancellationToken);

        await base.StopAsync(cancellationToken);
        _logger.Information("Worker auto discovery stopped");
    }

    /// <summary>
    /// Background task that detects dead worker instances (no heartbeat) and cleans up their consumer counts.
    /// Runs every 30 seconds to detect workers that haven't sent heartbeat for 60+ seconds.
    /// </summary>
    private async Task CleanupZombieWorkersAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Zombie worker cleanup task started (checks every 30s, timeout: 60s)");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                    // Get all workers from Redis
                    var workers = await _redisWorkerService.GetAllWorkersAsync(stoppingToken);

                    foreach (var worker in workers)
                    {
                        if (worker.Instances == null || worker.Instances.Count == 0)
                            continue;

                        var now = DateTime.UtcNow;
                        var heartbeatTimeoutSeconds = worker.Metadata.HeartbeatInterval * 3;
                        var deadInstances = worker.Instances.Where(i => i.LastHeartbeat != default && (now - i.LastHeartbeat).TotalSeconds > heartbeatTimeoutSeconds).ToList();

                        foreach (var deadInstance in deadInstances)
                        {
                            _logger.Warning("Detected dead worker instance: {InstanceId} (last heartbeat: {LastHeartbeat}, {Seconds}s ago)", deadInstance.InstanceId, deadInstance.LastHeartbeat, (now - deadInstance.LastHeartbeat).TotalSeconds);

                            // Cleanup consumer counts and instance metadata
                            var success = await _redisWorkerService.RemoveWorkerInstanceAsync(worker.WorkerId, deadInstance.InstanceId, stoppingToken);

                            if (success)
                            {
                                _logger.Information("Cleaned up dead worker instance: {InstanceId}", deadInstance.InstanceId);

                                _alertNotifier.SendFireAndForget(AlertType.WorkerDisconnected, new AlertPayload
                                {
                                    Title = "Worker Disconnected",
                                    Message = $"Worker instance '{deadInstance.InstanceId}' (worker: {worker.WorkerId}) disconnected. Last heartbeat was {(now - deadInstance.LastHeartbeat).TotalSeconds:F0}s ago.",
                                    Severity = AlertSeverity.Warning,
                                    Source = nameof(WorkerAutoDiscoveryService),
                                    ThreadKey = $"worker-disconnected-{deadInstance.InstanceId}",
                                    ActionLink = "/workers",
                                    AdditionalData = new
                                    {
                                        worker.WorkerId,
                                        deadInstance.InstanceId,
                                        deadInstance.LastHeartbeat,
                                        SecondsSinceLastHeartbeat = (now - deadInstance.LastHeartbeat).TotalSeconds
                                    }
                                });
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error during zombie worker cleanup");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Zombie worker cleanup task stopped");
        }
    }
}
