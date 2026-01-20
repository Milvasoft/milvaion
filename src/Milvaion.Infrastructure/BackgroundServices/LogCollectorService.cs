using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Consumes worker logs from RabbitMQ and appends them to JobOccurrence.Logs.
/// </summary>
public class LogCollectorService(IServiceProvider serviceProvider,
                                 IOptions<RabbitMQOptions> rabbitOptions,
                                 IOptions<LogCollectorOptions> logCollectorOptions,
                                 ILoggerFactory loggerFactory,
                                 IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, logCollectorOptions.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<LogCollectorService>();
    private readonly RabbitMQOptions _rabbitOptions = rabbitOptions.Value;
    private readonly LogCollectorOptions _options = logCollectorOptions.Value;
    private IConnection _connection;
    private IChannel _channel;
    private readonly static List<string> _updatePropNames = [nameof(JobOccurrence.Logs)];

    // Batch processing
    private readonly ConcurrentQueue<WorkerLogMessage> _logBatch = new();
    private readonly SemaphoreSlim _batchLock = new(1, 1);

    /// <inheritdoc/>
    protected override string ServiceName => "LogCollector";

    /// <inheritdoc />
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning("Log collection is disabled. Skipping startup.");

            return;
        }

        _logger.Information("Log collection starting...");

        // Start batch processor task
        var batchTask = Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(_options.BatchIntervalMs, stoppingToken);

                await ProcessBatchAsync(stoppingToken);

                TrackMemoryAfterIteration();
            }
        }, stoppingToken);

        var retryCount = 0;
        const int maxRetries = 10;
        const int retryDelaySeconds = 5;

        while (!stoppingToken.IsCancellationRequested && retryCount < maxRetries)
        {
            try
            {
                await ConnectAndConsumeAsync(stoppingToken);

                // If we reach here, connection was successful
                retryCount = 0;
            }
            catch (OperationCanceledException)
            {
                _logger.Information("Log collection is shutting down");

                break;
            }
            catch (Exception ex)
            {
                retryCount++;

                _logger.Error(ex, "LogCollectorService connection failed (attempt {Retry}/{MaxRetries})", retryCount, maxRetries);

                if (retryCount >= maxRetries)
                {
                    _logger.Fatal("LogCollectorService failed to connect after {MaxRetries} attempts. Service will be disabled until application restart.", maxRetries);

                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds * retryCount), stoppingToken);
            }
        }
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Setup RabbitMQ connection
        var factory = new ConnectionFactory
        {
            HostName = _rabbitOptions.Host,
            Port = _rabbitOptions.Port,
            UserName = _rabbitOptions.Username,
            Password = _rabbitOptions.Password,
            VirtualHost = _rabbitOptions.VirtualHost,
            AutomaticRecoveryEnabled = true,
            NetworkRecoveryInterval = TimeSpan.FromSeconds(10)
        };

        _connection = await factory.CreateConnectionAsync(stoppingToken);

        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declare queue (idempotent)
        await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.WorkerLogs,
                                         durable: true,
                                         exclusive: false,
                                         autoDelete: false,
                                         arguments: null,
                                         cancellationToken: stoppingToken);

        // Set prefetch count
        await _channel.BasicQosAsync(0, 10, false, stoppingToken);

        _logger.Information("Connected to RabbitMQ. Queue: {Queue}", WorkerConstant.Queues.WorkerLogs);

        // Setup consumer
        var consumer = new AsyncEventingBasicConsumer(_channel);

        consumer.ReceivedAsync += async (model, ea) =>
        {
            try
            {
                await ProcessLogMessageAsync(ea, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Unhandled exception in log consumer");
            }
        };

        await _channel.BasicConsumeAsync(queue: WorkerConstant.Queues.WorkerLogs,
                                         autoAck: false,
                                         consumer: consumer,
                                         cancellationToken: stoppingToken);

        _logger.Information("LogCollectorService is now consuming messages...");

        // Keep running until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task ProcessLogMessageAsync(BasicDeliverEventArgs ea, CancellationToken cancellationToken)
    {
        try
        {
            // Parse message
            var message = JsonSerializer.Deserialize<WorkerLogMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

            if (message == null)
            {
                _logger.Debug("Failed to deserialize log message");

                await _channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);

                return;
            }

            // Add to batch queue (NO DB operation here!)
            _logBatch.Enqueue(message);

            // Trigger immediate batch if queue is full
            if (_logBatch.Count >= _options.BatchSize)
            {
                await ProcessBatchAsync(cancellationToken);
            }

            // ACK the message
            await _channel.BasicAckAsync(ea.DeliveryTag, false, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to process log message");

            await _channel.BasicNackAsync(ea.DeliveryTag, false, false, cancellationToken);
        }
    }

    /// <summary>
    /// Process batch of logs - single DB transaction for all logs.
    /// </summary>
    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        // If batch processing is already in progress, skip
        if (!await _batchLock.WaitAsync(0, cancellationToken))
            return;

        try
        {
            if (_logBatch.IsEmpty)
                return;

            var batch = new List<WorkerLogMessage>();

            // Dequeue all pending logs
            while (_logBatch.TryDequeue(out var message))
                batch.Add(message);

            if (batch.Count == 0)
                return;

            try
            {
                await using var scope = _serviceProvider.CreateAsyncScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

                // Group by CorrelationId
                var logsByCorrelation = batch.GroupBy(m => m.CorrelationId).ToList();

                var correlationIds = logsByCorrelation.Select(g => g.Key).ToList();

                // Single query for all occurrences
                var occurrences = await dbContext.JobOccurrences.AsNoTracking()
                                                                .Where(o => correlationIds.Contains(o.CorrelationId))
                                                                .Select(JobOccurrence.Projections.UpdateLogs)
                                                                .ToListAsync(cancellationToken: cancellationToken);

                var occurrenceDict = occurrences.ToDictionary(o => o.CorrelationId);

                // Append logs in memory
                foreach (var group in logsByCorrelation)
                {
                    if (occurrenceDict.TryGetValue(group.Key, out var occurrence))
                    {
                        foreach (var message in group)
                            occurrence.Logs.Add(message.Log);
                    }
                    else
                    {
                        _logger.Debug("JobOccurrence not found for CorrelationId: {CorrelationId}", group.Key);
                    }
                }

                await dbContext.BulkUpdateAsync(occurrences, (bc) =>
                {
                    bc.PropertiesToInclude = bc.PropertiesToIncludeOnUpdate = _updatePropNames;
                }, cancellationToken: cancellationToken);

                #region Send Socket Events

                // Trigger SignalR events after DB update
                var eventPublisher = scope.ServiceProvider.GetService<IJobOccurrenceEventPublisher>();

                if (eventPublisher != null)
                {
                    //  Collect events first, then publish in batch
                    var publishTasks = new List<Task>(batch.Count);

                    foreach (var kvp in logsByCorrelation)
                        if (occurrenceDict.TryGetValue(kvp.Key, out var occurrence))
                            foreach (var message in kvp)
                                publishTasks.Add(eventPublisher.PublishLogAddedAsync(occurrence.Id, message.Log, cancellationToken));

                    // Wait for all events to complete (with timeout)
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                    var completedTask = await Task.WhenAny(Task.WhenAll(publishTasks), timeoutTask);

                    if (completedTask == timeoutTask)
                        _logger.Warning("SignalR event publishing timed out after 5 seconds for {Count} events", publishTasks.Count);
                }

                #endregion

                _logger.Debug("Processed {Count} logs in batch", batch.Count);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to process log batch");
            }
        }
        finally
        {
            // Release the batch lock
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
        _logger.Information("LogCollectorService stopping...");

        // Process remaining logs before shutdown
        await ProcessBatchAsync(cancellationToken);

        // Dispose semaphore
        _batchLock?.Dispose();

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
    }
}
