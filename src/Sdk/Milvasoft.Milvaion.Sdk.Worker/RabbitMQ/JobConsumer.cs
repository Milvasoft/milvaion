using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Core;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Indicates how to handle the message after processing.
/// </summary>
internal enum MessageDisposition
{
    /// <summary>
    /// Acknowledge the message (success).
    /// </summary>
    Ack,

    /// <summary>
    /// Negative acknowledge (send to DLQ).
    /// </summary>
    Nack,

    /// <summary>
    /// Already handled (ACK already sent in Retry/DLQ flow).
    /// </summary>
    AlreadyHandled
}

/// <summary>
/// Background service that consumes jobs from RabbitMQ queue.
/// </summary>
public class JobConsumer : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IMilvaLogger _logger;
    private readonly WorkerOptions _options;
    private readonly Dictionary<string, JobConsumerConfig> _jobConfigs;
    private IConnection _connection;
    private IChannel _channel;

    // Concurrency control
    private SemaphoreSlim _concurrencySemaphore;

    // Track running job count for graceful shutdown (not tasks, just count)
    private int _runningJobCount;

    // Channel thread-safety: IChannel is NOT thread-safe, serialize ACK/NACK/Publish operations
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    // Offline resilience (optional - null if disabled)
    private readonly OutboxService _outboxService;

    public JobConsumer(IServiceProvider serviceProvider,
                       IOptions<WorkerOptions> options,
                       IOptions<JobConsumerOptions> jobConsumerOptions,
                       IMilvaLogger logger,
                       OutboxService outboxService = null)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options.Value;
        _jobConfigs = jobConsumerOptions.Value ?? new Dictionary<string, JobConsumerConfig>();
        _outboxService = outboxService;

        _logger.Debug($"[JOBCONSUMER INIT] JobConsumerOptions.Value is null: {jobConsumerOptions.Value == null}");
        _logger.Debug($"[JOBCONSUMER INIT] Consumers count: {_jobConfigs.Count}");
        _logger.Debug($"[JOBCONSUMER INIT] OutboxService: {(_outboxService != null ? "Enabled" : "Disabled")}");

        foreach (var (jobName, config) in _jobConfigs)
            _logger.Debug($"[JOBCONSUMER INIT]   - {jobName}: Timeout={config.ExecutionTimeoutSeconds}s");
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var maxParallelJobs = _options.MaxParallelJobs;

        _concurrencySemaphore = new SemaphoreSlim(maxParallelJobs, maxParallelJobs);

        // Generate queue name from routing patterns
        var queueSuffix = _options.RabbitMQ.RoutingKeyPattern.Replace("*", "wildcard").Replace("#", "all");
        var queueName = $"{WorkerConstant.Queues.Jobs}.{queueSuffix}";

        _logger?.Information("Worker {InstanceId} starting consumer for patterns [{Patterns}]. Queue: {QueueName}. MaxParallelJobs: {MaxParallel}", _options.InstanceId, _options.RabbitMQ.RoutingKeyPattern, queueName, maxParallelJobs);

        try
        {
            // Setup RabbitMQ connection with explicit heartbeat configuration
            // Critical for long-running jobs (3+ hours) to prevent connection drops
            var factory = new ConnectionFactory
            {
                HostName = _options.RabbitMQ.Host,
                Port = _options.RabbitMQ.Port,
                UserName = _options.RabbitMQ.Username,
                Password = _options.RabbitMQ.Password,
                VirtualHost = _options.RabbitMQ.VirtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                // Heartbeat configuration for long-running jobs
                // Server sends heartbeat every 60s, client responds
                // If no response in 2x heartbeat interval, connection is considered dead
                RequestedHeartbeat = TimeSpan.FromSeconds(60),
                // Consumer dispatch concurrency - allows heartbeats to be processed
                // even when consumer callbacks are busy with long-running jobs
                ConsumerDispatchConcurrency = (ushort)(maxParallelJobs + 1)
            };

            _connection = await factory.CreateConnectionAsync(stoppingToken);
            _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

            await _channel.ExchangeDeclareAsync(exchange: WorkerConstant.ExchangeName,
                                                type: "topic",
                                                durable: true,
                                                autoDelete: false,
                                                arguments: null,
                                                cancellationToken: stoppingToken);

            // Create DLX (Dead Letter Exchange)
            await _channel.ExchangeDeclareAsync(exchange: WorkerConstant.DeadLetterExchangeName,
                                                type: "direct",
                                                durable: true,
                                                autoDelete: false,
                                                arguments: null,
                                                cancellationToken: stoppingToken);

            // Create DLQ (Dead Letter Queue)
            await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.FailedOccurrences,
                                             durable: true,
                                             exclusive: false,
                                             autoDelete: false,
                                             arguments: null,
                                             cancellationToken: stoppingToken);

            // Bind DLQ to DLX
            await _channel.QueueBindAsync(queue: WorkerConstant.Queues.FailedOccurrences,
                                          exchange: WorkerConstant.DeadLetterExchangeName,
                                          routingKey: WorkerConstant.DeadLetterRoutingKey,
                                          arguments: null,
                                          cancellationToken: stoppingToken);

            // Worker queue with DLX settings
            var queueArgs = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", WorkerConstant.DeadLetterExchangeName },
                { "x-dead-letter-routing-key", WorkerConstant.DeadLetterRoutingKey }
            };

            await _channel.QueueDeclareAsync(queue: queueName,
                                             durable: true,
                                             exclusive: false,
                                             autoDelete: false,
                                             arguments: queueArgs,
                                             cancellationToken: stoppingToken);

            await _channel.QueueBindAsync(queue: queueName,
                                          exchange: WorkerConstant.ExchangeName,
                                          routingKey: _options.RabbitMQ.RoutingKeyPattern,
                                          arguments: null,
                                          cancellationToken: stoppingToken);

            _logger?.Information("Worker {InstanceId} bound queue '{Queue}' to exchange '{Exchange}' with pattern '{Pattern}'", _options.InstanceId, queueName, WorkerConstant.ExchangeName, _options.RabbitMQ.RoutingKeyPattern);

            _logger?.Information("Worker {InstanceId} DLQ configured. DLX: {DLX}, DLQ: {DLQ}, RoutingKey: {RoutingKey}", _options.InstanceId, WorkerConstant.DeadLetterExchangeName, WorkerConstant.Queues.FailedOccurrences, WorkerConstant.DeadLetterRoutingKey);

            await _channel.BasicQosAsync(0, (ushort)maxParallelJobs, false, stoppingToken);

            _logger?.Information("Worker {InstanceId} connected. Queue: {Queue}, Patterns: [{Patterns}], MaxParallel: {MaxParallel}", _options.InstanceId, queueName, _options.RabbitMQ.RoutingKeyPattern, maxParallelJobs);

            var consumer = new AsyncEventingBasicConsumer(_channel);

            consumer.ReceivedAsync += async (model, ea) =>
            {
                // No Task.Run needed - AsyncEventingBasicConsumer already dispatches async
                // Semaphore controls concurrency, ConsumerDispatchConcurrency allows heartbeats
                await ProcessMessageWithConcurrencyControlAsync(ea, stoppingToken);
            };

            await _channel.BasicConsumeAsync(queue: queueName,
                                             autoAck: false,
                                             consumer: consumer,
                                             cancellationToken: stoppingToken);

            _logger?.Information("Worker {InstanceId} is now consuming from queue '{Queue}'", _options.InstanceId, queueName);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger?.Information("Worker {InstanceId} is shutting down, waiting for {Count} running jobs...", _options.InstanceId, _runningJobCount);

            // Wait for all running jobs to complete using semaphore
            // Acquire all slots = all jobs finished
            for (int i = 0; i < _options.MaxParallelJobs; i++)
                await _concurrencySemaphore.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

            _logger?.Information("Worker {InstanceId} all jobs completed", _options.InstanceId);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Fatal error in JobConsumer");
            throw;
        }
    }

    /// <inheritdoc/>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger?.Information("Worker {InstanceId} stopping...", _options.InstanceId);

        if (_channel != null)
        {
            await _channel.CloseAsync(cancellationToken);
            _channel.Dispose();
        }

        if (_connection != null)
        {
            await _connection.CloseAsync(cancellationToken);
            _connection.Dispose();
        }

        await base.StopAsync(cancellationToken);

        _logger?.Information("Worker {InstanceId} stopped", _options.InstanceId);
    }

    /// <summary>
    /// Process message with semaphore-based concurrency control.
    /// No Task.Run needed - async I/O doesn't block threads.
    /// </summary>
    private async Task ProcessMessageWithConcurrencyControlAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        // Wait for available slot
        await _concurrencySemaphore.WaitAsync(cancellationToken);
        Interlocked.Increment(ref _runningJobCount);

        try
        {
            await ProcessMessageAsync(ea, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _runningJobCount);
            _concurrencySemaphore.Release();
        }
    }

    private async Task ProcessMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        var correlationId = Guid.Empty;
        IMilvaLogger logger = null;
        Guid jobId = Guid.Empty;

        try
        {
            // Parse message
            var job = JsonSerializer.Deserialize<ScheduledJob>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);
            jobId = job.Id;

            // Create scope for DI services
            await using var scope = _serviceProvider.CreateAsyncScope();

            logger = scope.ServiceProvider.GetService<IMilvaLogger>();

            // Get CorrelationId from headers (safe parsing)
            correlationId = ParseCorrelationId(ea.BasicProperties);

            // Get retry count from headers (safe parsing)
            var retryCount = ParseRetryCount(ea.BasicProperties);

            logger?.Debug("Received job {JobId} (Type: {JobType}, CorrelationId: {CorrelationId}, RetryCount: {RetryCount})", job.Id, job.JobNameInWorker, correlationId, retryCount);

            // IDEMPOTENCY CHECK: Skip if job already finalized in local store
            if (_outboxService != null)
            {
                var isFinalized = await _outboxService.GetLocalStore().IsJobFinalizedAsync(correlationId, cancellationToken);

                logger?.Debug("[IDEMPOTENCY CHECK] Job {JobId} (CorrelationId: {CorrelationId}) - IsFinalized: {IsFinalized}", job.Id, correlationId, isFinalized);

                if (isFinalized)
                {
                    logger?.Debug("Job {JobId} (CorrelationId: {CorrelationId}) already finalized. Skipping redelivery and ACK-ing message.", job.Id, correlationId);

                    // ACK the message to prevent further redelivery (no cancellation)
                    await SafeAckAsync(ea.DeliveryTag, logger, job.Id);

                    return;
                }
            }

            // REDELIVERY DETECTION: Log warning if message was redelivered (worker may have crashed)
            if (ea.Redelivered)
            {
                logger?.Warning("Job {JobId} redelivered (CorrelationId: {CorrelationId}). Previous execution may have crashed or timed out. Job will be re-executed from the beginning.", job.Id, correlationId);

                // Publish log to occurrence for visibility in UI
                if (_outboxService != null)
                {
                    try
                    {
                        await _outboxService.PublishStatusUpdateAsync(correlationId,
                                                                      job.Id,
                                                                      _options.InstanceId,
                                                                      JobOccurrenceStatus.Running,
                                                                      startTime: DateTime.UtcNow,
                                                                      cancellationToken: CancellationToken.None);

                        var redeliveryLog = new OccurrenceLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Warning",
                            Message = "⚠️ Job redelivered - previous execution may have crashed or timed out. Re-executing from the beginning.",
                            Data = new Dictionary<string, object>
                            {
                                ["Reason"] = "RabbitMQ redelivery detected",
                                ["WorkerId"] = _options.InstanceId
                            },
                            Category = "Redelivery"
                        };

                        await _outboxService.PublishLogAsync(correlationId, _options.InstanceId, redeliveryLog, CancellationToken.None);

                        logger?.Debug("Redelivery log and status update published for CorrelationId {CorrelationId}", correlationId);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warning(ex, "Failed to publish redelivery log and status update (non-critical)");
                    }
                }
            }

            // Process the job and get disposition
            var disposition = await ProcessJobAsync(job, correlationId, ea.DeliveryTag, scope.ServiceProvider, retryCount, logger, cancellationToken);

            // Handle message based on disposition (no cancellation for ACK/NACK)
            switch (disposition)
            {
                case MessageDisposition.Ack:
                    await SafeAckAsync(ea.DeliveryTag, logger, job.Id);
                    break;

                case MessageDisposition.Nack:
                    await SafeNackAsync(ea.DeliveryTag, logger, correlationId);
                    break;

                case MessageDisposition.AlreadyHandled:
                    // ACK was already sent in Retry/DLQ flow - do nothing
                    logger?.Debug("Job {JobId} disposition: AlreadyHandled (ACK sent during Retry/DLQ flow)", job.Id);
                    break;
            }
        }
        catch (JsonException jsonEx)
        {
            // Bad message format - NACK without requeue (send to DLQ)
            _logger?.Error(jsonEx, "Failed to deserialize message. Sending to DLQ.");
            await SafeNackAsync(ea.DeliveryTag, _logger, correlationId);
        }
        catch (Exception ex)
        {
            var loggerToUse = logger ?? _logger;
            loggerToUse?.Error(ex, "Failed to process job {JobId} (CorrelationId: {CorrelationId}). NACK-ing message.", jobId, correlationId);
            await SafeNackAsync(ea.DeliveryTag, loggerToUse, correlationId);
        }
    }

    /// <summary>
    /// Safely parse CorrelationId from message headers.
    /// </summary>
    private static Guid ParseCorrelationId(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers != null && properties.Headers.TryGetValue("CorrelationId", out var correlationIdObj))
        {
            var correlationIdStr = correlationIdObj switch
            {
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                string str => str,
                _ => null
            };

            if (Guid.TryParse(correlationIdStr, out var correlationId))
                return correlationId;
        }

        if (!string.IsNullOrEmpty(properties.CorrelationId) && Guid.TryParse(properties.CorrelationId, out var fallbackId))
            return fallbackId;

        return Guid.Empty;
    }

    /// <summary>
    /// Safely parse retry count from message headers.
    /// </summary>
    private static int ParseRetryCount(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers == null || !properties.Headers.TryGetValue("x-retry-count", out var retryObj))
            return 0;

        return retryObj switch
        {
            byte[] bytes when bytes.Length >= 4 => BitConverter.ToInt32(bytes, 0),
            int i => i,
            long l => (int)l,
            _ => 0
        };
    }

    /// <summary>
    /// Thread-safe ACK operation. IChannel is NOT thread-safe, so we serialize operations.
    /// CRITICAL: ACK must NEVER be cancelled - it's a protocol-level finalizer.
    /// </summary>
    private async Task SafeAckAsync(ulong deliveryTag, IMilvaLogger logger, Guid jobId)
    {
        if (_channel == null || !_channel.IsOpen)
            return;

        // Use short timeout for lock acquisition, but NEVER cancel the ACK itself
        using var lockCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _channelLock.WaitAsync(lockCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger?.Warning("Timeout waiting for channel lock for ACK. Job {JobId} may be redelivered.", jobId);
            return;
        }

        try
        {
            if (_channel != null && _channel.IsOpen)
            {
                // NEVER pass cancellationToken here - ACK must complete
                await _channel.BasicAckAsync(deliveryTag, multiple: false, CancellationToken.None);
                logger?.Debug("Job {JobId} acknowledged (ACK)", jobId);
            }
        }
        catch (ObjectDisposedException)
        {
            logger?.Warning("Channel disposed before ACK for job {JobId}, but job completed successfully", jobId);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    /// <summary>
    /// Thread-safe NACK operation. IChannel is NOT thread-safe, so we serialize operations.
    /// CRITICAL: NACK must NEVER be cancelled - it's a protocol-level finalizer.
    /// </summary>
    private async Task SafeNackAsync(ulong deliveryTag, IMilvaLogger logger, Guid correlationId)
    {
        if (_channel == null || !_channel.IsOpen)
            return;

        // Use short timeout for lock acquisition, but NEVER cancel the NACK itself
        using var lockCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await _channelLock.WaitAsync(lockCts.Token);
        }
        catch (OperationCanceledException)
        {
            logger?.Warning("Timeout waiting for channel lock for NACK. CorrelationId {CorrelationId} may be redelivered.", correlationId);
            return;
        }

        try
        {
            if (_channel != null && _channel.IsOpen)
            {
                // NEVER pass cancellationToken here - NACK must complete
                await _channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, CancellationToken.None);
                logger?.Debug("Message NACK'd for CorrelationId {CorrelationId}", correlationId);
            }
        }
        catch (ObjectDisposedException)
        {
            logger?.Warning("Channel disposed before NACK for CorrelationId {CorrelationId}", correlationId);
        }
        catch (Exception nackEx)
        {
            logger?.Error(nackEx, "Failed to NACK message for CorrelationId {CorrelationId}", correlationId);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    /// <summary>
    /// Thread-safe publish operation. IChannel is NOT thread-safe.
    /// For DLQ publishes, we should not cancel - message must be delivered.
    /// </summary>
    private async Task SafePublishAsync(string exchange, string routingKey, BasicProperties properties, byte[] body, bool allowCancellation = false, CancellationToken cancellationToken = default)
    {
        if (_channel == null || !_channel.IsOpen)
            throw new InvalidOperationException("RabbitMQ channel is not open. Cannot publish message.");

        // Use appropriate token based on whether cancellation is allowed
        var tokenToUse = allowCancellation ? cancellationToken : CancellationToken.None;

        // Use short timeout for lock acquisition
        using var lockCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await _channelLock.WaitAsync(lockCts.Token);

        try
        {
            if (_channel == null || !_channel.IsOpen)
                throw new InvalidOperationException("RabbitMQ channel closed while waiting for lock.");

            await _channel.BasicPublishAsync(exchange: exchange,
                                             routingKey: routingKey,
                                             mandatory: false,
                                             basicProperties: properties,
                                             body: body,
                                             cancellationToken: tokenToUse);
        }
        finally
        {
            _channelLock.Release();
        }
    }

    /// <summary>
    /// Process job and return disposition for message handling.
    /// </summary>
    private async Task<MessageDisposition> ProcessJobAsync(ScheduledJob job,
                                                           Guid correlationId,
                                                           ulong deliveryTag,
                                                           IServiceProvider scopedProvider,
                                                           int retryCount,
                                                           IMilvaLogger logger,
                                                           CancellationToken cancellationToken)
    {
        var jobExecutor = scopedProvider.GetRequiredService<JobExecutor>();
        var jobTracker = scopedProvider.GetRequiredService<WorkerJobTracker>();

        // Record job start in local store (if outbox service available)
        if (_outboxService != null)
        {
            try
            {
                await _outboxService.GetLocalStore().RecordJobStartAsync(correlationId, job.Id, job.JobNameInWorker, _options.InstanceId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "Failed to record job start in local store");
            }
        }

        _logger?.Debug("[JOB START] Processing job {JobId} (Type: {JobType}, CorrelationId: {CorrelationId}, RetryCount: {RetryCount})", job.Id, job.JobNameInWorker, correlationId, retryCount);

        // Track job start
        jobTracker.IncrementJobCount(_options.InstanceId);
        _logger?.Debug("[JOB START] Current job count: {JobCount}", jobTracker.GetJobCount(_options.InstanceId));

        await SendImmediateHeartbeatAsync();

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var jobCancellationToken = jobCts.Token;

        CancellationListenerService.RegisterJob(correlationId, jobCts);

        try
        {
            // Publish "Running" status
            if (_outboxService != null)
            {
                try
                {
                    await _outboxService.PublishStatusUpdateAsync(correlationId,
                                                                  job.Id,
                                                                  _options.InstanceId,
                                                                  JobOccurrenceStatus.Running,
                                                                  startTime: DateTime.UtcNow,
                                                                  cancellationToken: CancellationToken.None);
                }
                catch (Exception ex)
                {
                    logger?.Warning(ex, "Failed to publish Running status");
                }
            }

            var jobInstance = ResolveJob(job.JobNameInWorker, scopedProvider);

            if (jobInstance == null)
            {
                logger?.Error("No IJob implementation found for JobType: {JobType}", job.JobNameInWorker);

                if (_outboxService != null)
                {
                    try
                    {
                        await _outboxService.PublishStatusUpdateAsync(correlationId,
                                                                      job.Id,
                                                                      _options.InstanceId,
                                                                      JobOccurrenceStatus.Failed,
                                                                      endTime: DateTime.UtcNow,
                                                                      exception: $"No IJob implementation registered for type: {job.JobNameInWorker}",
                                                                      cancellationToken: CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger?.Warning(ex, "Failed to publish Failed status");
                    }
                }

                return MessageDisposition.Nack;
            }

            // Execute the job
            var result = await jobExecutor.ExecuteAsync(jobInstance,
                                                        job,
                                                        correlationId,
                                                        _outboxService,
                                                        _options,
                                                        _jobConfigs.GetValueOrDefault(job.JobNameInWorker),
                                                        jobCancellationToken);

            // Retry logic: If failed and under max retries, republish with incremented retry count
            // Skip retry for permanent failures (e.g., invalid data, business rule violations)
            if (result.Status == JobOccurrenceStatus.Failed && !string.IsNullOrEmpty(result.Exception) && !result.IsPermanentFailure)
            {
                var maxRetries = 3;
                var baseDelaySeconds = 5;

                if (_jobConfigs.TryGetValue(job.JobNameInWorker, out var retryConfig))
                {
                    maxRetries = retryConfig.MaxRetries;
                    baseDelaySeconds = retryConfig.BaseRetryDelaySeconds;
                }

                if (retryCount < maxRetries)
                {
                    // Retry: Republish to main queue with incremented retry count
                    var delaySeconds = (int)Math.Pow(2, retryCount) * baseDelaySeconds;

                    logger?.Debug("Job {JobId} failed (attempt {Attempt}/{MaxRetries}). Retrying in {Delay}s. Exception: {Exception}", job.Id, retryCount + 1, maxRetries, delaySeconds, result.Exception);

                    // Publish retry info to logs
                    if (_outboxService != null)
                    {
                        try
                        {
                            var retryLog = new OccurrenceLog
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Warning",
                                Message = $"⚠️ Job failed (attempt {retryCount + 1}/{maxRetries}). Retrying in {delaySeconds}s...",
                                Data = new Dictionary<string, object>
                                {
                                    ["RetryAttempt"] = retryCount + 1,
                                    ["MaxRetries"] = maxRetries,
                                    ["DelaySeconds"] = delaySeconds,
                                    ["NextRetryTime"] = DateTime.UtcNow.AddSeconds(delaySeconds)
                                },
                                Category = "Retry"
                            };

                            await _outboxService.PublishLogAsync(correlationId, _options.InstanceId, retryLog, CancellationToken.None);
                        }
                        catch { /* Ignore log failure */ }
                    }

                    await RetryJobAsync(job, correlationId, retryCount + 1, maxRetries, delaySeconds, cancellationToken);

                    // ACK original message NOW (retry message published)
                    await SafeAckAsync(deliveryTag, logger, job.Id);

                    return MessageDisposition.AlreadyHandled;
                }
                else
                {
                    // Max retries exceeded → DLQ
                    logger?.Debug("Job {JobId} failed after {MaxRetries} retries. Moving to DLQ.", job.Id, maxRetries);

                    if (_outboxService != null)
                    {
                        try
                        {
                            var dlqLog = new OccurrenceLog
                            {
                                Timestamp = DateTime.UtcNow,
                                Level = "Error",
                                Message = $"❌ Job failed after {maxRetries} retries. Moving to Dead Letter Queue (DLQ).",
                                Data = new Dictionary<string, object>
                                {
                                    ["TotalRetries"] = maxRetries,
                                    ["FinalStatus"] = "MovedToDLQ"
                                },
                                Category = "Retry"
                            };

                            await _outboxService.PublishLogAsync(correlationId, _options.InstanceId, dlqLog, CancellationToken.None);
                        }
                        catch { /* Ignore log failure */ }

                        result = result with
                        {
                            Status = JobOccurrenceStatus.Failed,
                            EndTime = DateTime.UtcNow,
                            DurationMs = (long)(DateTime.UtcNow - result.StartTime).TotalMilliseconds,
                            Result = null,
                            Exception = $"Max retries ({maxRetries}) exceeded. Original exception: {result.Exception}"
                        };

                        await PublishFinalStatusAndFinalizeAsync(correlationId, job.Id, result, logger);
                    }

                    // Publish to DLQ
                    await PublishToDLQAsync(new DlqJobMessage
                    {
                        Id = job.Id,
                        JobNameInWorker = job.JobNameInWorker,
                        DisplayName = job.DisplayName,
                        JobData = job.JobData,
                        ExecuteAt = job.ExecuteAt,
                        Exception = result.Exception,
                        Status = result.Status
                    }, correlationId, maxRetries, maxRetries, cancellationToken);

                    // ACK original message NOW (DLQ message published)
                    await SafeAckAsync(deliveryTag, logger, job.Id);

                    return MessageDisposition.AlreadyHandled;
                }
            }
            // Handle permanent failures - skip retry and go directly to DLQ
            else if (result.Status == JobOccurrenceStatus.Failed && result.IsPermanentFailure)
            {
                logger?.Debug("Job {JobId} failed with permanent error. Skipping retry, moving to DLQ. Exception: {Exception}", job.Id, result.Exception);

                if (_outboxService != null)
                {
                    try
                    {
                        var dlqLog = new OccurrenceLog
                        {
                            Timestamp = DateTime.UtcNow,
                            Level = "Error",
                            Message = "❌ Permanent failure - job will not be retried. Moving to Dead Letter Queue (DLQ).",
                            Data = new Dictionary<string, object>
                            {
                                ["FailureType"] = "Permanent",
                                ["FinalStatus"] = "MovedToDLQ"
                            },
                            Category = "Retry"
                        };

                        await _outboxService.PublishLogAsync(correlationId, _options.InstanceId, dlqLog, CancellationToken.None);
                    }
                    catch { /* Ignore log failure */ }

                    await PublishFinalStatusAndFinalizeAsync(correlationId, job.Id, result, logger);
                }

                // Publish to DLQ
                await PublishToDLQAsync(new DlqJobMessage
                {
                    Id = job.Id,
                    JobNameInWorker = job.JobNameInWorker,
                    DisplayName = job.DisplayName,
                    JobData = job.JobData,
                    ExecuteAt = job.ExecuteAt,
                    Exception = $"[PERMANENT FAILURE] {result.Exception}",
                    Status = result.Status
                }, correlationId, retryCount, 0, cancellationToken); // maxRetries=0 indicates permanent failure

                // ACK original message NOW (DLQ message published)
                await SafeAckAsync(deliveryTag, logger, job.Id);

                return MessageDisposition.AlreadyHandled;
            }

            // Publish final status for successful/cancelled/timeout jobs
            await PublishFinalStatusAndFinalizeAsync(correlationId, job.Id, result, logger);

            return MessageDisposition.Ack;
        }
        finally
        {
            CancellationListenerService.UnregisterJob(correlationId);

            _logger?.Debug("[JOB END] Decrementing job count for InstanceId: {InstanceId}", _options.InstanceId);
            jobTracker.DecrementJobCount(_options.InstanceId);
            _logger?.Debug("[JOB END] Current job count: {JobCount}", jobTracker.GetJobCount(_options.InstanceId));

            await SendImmediateHeartbeatAsync();
        }
    }

    /// <summary>
    /// Sends an immediate heartbeat to update Redis with current job count.
    /// </summary>
    private async Task SendImmediateHeartbeatAsync()
    {
        try
        {
            var workerPublisher = _serviceProvider.GetServices<IHostedService>().OfType<WorkerListenerPublisher>().FirstOrDefault();

            if (workerPublisher != null)
                await workerPublisher.SendAllHeartbeatsAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            // Channel disposed during shutdown - ignore
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "Failed to send immediate heartbeat (non-critical)");
        }
    }

    private static IJobBase ResolveJob(string jobType, IServiceProvider serviceProvider)
    {
        var jobs = serviceProvider.GetServices<IJobBase>();

        return jobs.FirstOrDefault(j => j.GetType().Name.Equals(jobType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Retries a failed job by republishing it to the main queue with incremented retry count and delay.
    /// </summary>
    private async Task RetryJobAsync(ScheduledJob job,
                                     Guid correlationId,
                                     int newRetryCount,
                                     int maxRetries,
                                     int delaySeconds,
                                     CancellationToken cancellationToken)
    {
        try
        {
            var queueSuffix = _options.RabbitMQ.RoutingKeyPattern.Replace("*", "wildcard").Replace("#", "all");
            var queueName = $"{WorkerConstant.Queues.Jobs}.{queueSuffix}";

            var json = JsonSerializer.Serialize(job);
            var body = Encoding.UTF8.GetBytes(json);

            var properties = new BasicProperties
            {
                Persistent = true,
                CorrelationId = correlationId.ToString(),
                Headers = new Dictionary<string, object>
                {
                    { "CorrelationId", Encoding.UTF8.GetBytes(correlationId.ToString()) },
                    { "x-retry-count", newRetryCount },
                    { "MaxRetries", maxRetries }
                }
            };

            // Delay before republishing (exponential backoff)
            // Note: If worker restarts during delay, retry will be lost - this is a known trade-off
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

            await SafePublishAsync(exchange: "",
                                   routingKey: queueName,
                                   properties: properties,
                                   body: body,
                                   cancellationToken: cancellationToken);

            _logger?.Debug("Job {JobId} republished for retry {RetryCount} after {Delay}s", job.Id, newRetryCount, delaySeconds);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to republish job {JobId} for retry", job.Id);
            throw;
        }
    }

    /// <summary>
    /// Publishes a failed job directly to DLQ with retry count headers.
    /// </summary>
    private async Task PublishToDLQAsync(DlqJobMessage message,
                                         Guid correlationId,
                                         int retryCount,
                                         int maxRetries,
                                         CancellationToken cancellationToken)
    {
        try
        {
            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            var properties = new BasicProperties
            {
                Persistent = true,
                CorrelationId = correlationId.ToString(),
                Headers = new Dictionary<string, object>
                {
                    { "CorrelationId", Encoding.UTF8.GetBytes(correlationId.ToString()) },
                    { "x-retry-count", retryCount },
                    { "MaxRetries", maxRetries }
                }
            };

            await SafePublishAsync(exchange: WorkerConstant.DeadLetterExchangeName,
                                   routingKey: WorkerConstant.DeadLetterRoutingKey,
                                   properties: properties,
                                   body: body,
                                   cancellationToken: cancellationToken);

            _logger?.Debug("Job {JobId} published to DLQ with retry count {RetryCount}/{MaxRetries}", message.Id, retryCount, maxRetries);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to publish job {JobId} to DLQ", message.Id);
            throw;
        }
    }

    /// <summary>
    /// Publishes final job status and finalizes in local store.
    /// </summary>
    private async Task PublishFinalStatusAndFinalizeAsync(Guid correlationId, Guid jobId, JobExecutionResult result, IMilvaLogger logger)
    {
        if (_outboxService != null)
        {
            try
            {
                await _outboxService.PublishStatusUpdateAsync(correlationId,
                                                              jobId,
                                                              _options.InstanceId,
                                                              result.Status,
                                                              result.StartTime,
                                                              result.EndTime,
                                                              result.DurationMs,
                                                              result.Result,
                                                              result.Exception,
                                                              CancellationToken.None);

                await _outboxService.GetLocalStore().FinalizeJobAsync(correlationId, result.Status, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger?.Warning(ex, "Failed to publish final status or finalize job");
            }
        }
    }
}
