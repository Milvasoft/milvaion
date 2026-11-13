using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.Secrets;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.BackgroundServices.Base;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.Infrastructure.Services.Secrets;
using Milvaion.Infrastructure.Telemetry;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Milvaion.Infrastructure.BackgroundServices;

/// <summary>
/// Background service that consumes failed jobs from Dead Letter Queue and stores them in database.
/// Provides visibility and manual recovery capability for jobs that exceeded max retry attempts.
/// </summary>
public class FailedOccurrenceHandler(IServiceProvider serviceProvider,
                                     IMessageConsumerTransport transport,
                                     IAlertNotifier alertNotifier,
                                     IOptions<FailedOccurrenceHandlerOptions> options,
                                     ILoggerFactory loggerFactory,
                                     BackgroundServiceMetrics metrics,
                                     IMemoryStatsRegistry memoryStatsRegistry = null) : MemoryTrackedBackgroundService(loggerFactory, options.Value, memoryStatsRegistry)
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly ISecretMasker _secretMasker = serviceProvider.GetService<ISecretMasker>();
    private readonly ISecretTemplateStore _secretTemplateStore = serviceProvider.GetService<ISecretTemplateStore>();
    private readonly IMessageConsumerTransport _transport = transport;
    private readonly IAlertNotifier _alertNotifier = alertNotifier;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<FailedOccurrenceHandler>();
    private readonly FailedOccurrenceHandlerOptions _options = options.Value;
    private readonly BackgroundServiceMetrics _metrics = metrics;
    private IMessageConsumer _consumer;
    private const int _maxExceptionLength = 3000;
    private static readonly TimeSpan _transientFailureDelay = TimeSpan.FromSeconds(5);

    /// <inheritdoc/>
    protected override string ServiceName => "FailedOccurrenceHandler";

    /// <inheritdoc />
    protected override async Task ExecuteWithMemoryTrackingAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _logger.Warning(nameof(FailedOccurrenceHandler) + " is disabled. Skipping startup.");

            return;
        }

        _logger.Information("FailedOccurrenceHandler started. Listening to DLQ: {DLQ}", WorkerConstant.Queues.FailedOccurrences);

        await RabbitMQChannelExtensions.RunConsumerLoopAsync(ConnectAndConsumeAsync, _logger, ServiceName, failures =>
        {
            _alertNotifier.SendFireAndForget(AlertType.ServiceDegraded, new AlertPayload
            {
                Title = "FailedOccurrenceHandler Service Degraded",
                Message = $"FailedOccurrenceHandler failed to connect to {_transport.Name} after {failures} attempts. The DLQ is not being processed; the service keeps retrying.",
                Severity = AlertSeverity.Critical,
                Source = nameof(FailedOccurrenceHandler),
                ThreadKey = "service-degraded-failedoccurrencehandler"
            });
        }, stoppingToken);
    }

    private async Task ConnectAndConsumeAsync(CancellationToken stoppingToken)
    {
        // Process one message at a time. The DLQ is declared with its dead letter binding at startup, so it is not redeclared here.
        var consumer = await _transport.StartConsumerAsync(new ConsumerSubscription(WorkerConstant.Queues.FailedOccurrences, 1, ServiceName, DeclareQueue: false),
                                                           delivery => HandleDeliveryAsync(delivery, stoppingToken),
                                                           stoppingToken);

        _consumer = consumer;

        _logger.Information("FailedOccurrenceHandler consumer registered for DLQ: {DLQ}", WorkerConstant.Queues.FailedOccurrences);

        await consumer.Completion.WaitAsync(stoppingToken);

        await consumer.DisposeAsync();
    }

    private async Task HandleDeliveryAsync(IMessageDelivery delivery, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessFailedOccurrenceAsync(delivery, stoppingToken);

            // Acknowledge successful processing
            await delivery.AckAsync(stoppingToken);

            TrackMemoryAfterIteration();
        }
        catch (Exception ex) when (ex is ArgumentException || RabbitMQChannelExtensions.IsPermanentDataError(ex))
        {
            // A malformed message or data the database rejects: requeueing would fail the same way forever.
            _logger.Error(ex, "Dropping DLQ message that cannot be stored. DeliveryTag: {DeliveryTag}, MessageId: {MessageId}", delivery.DeliveryTag, delivery.MessageId);

            await delivery.NackAsync(requeue: false, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Not acked, so the broker redelivers it after the restart.
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error processing failed job from DLQ. DeliveryTag: {DeliveryTag}", delivery.DeliveryTag);

            // With prefetch 1 this pause also throttles the redelivery loop during an outage.
            await Task.Delay(_transientFailureDelay, CancellationToken.None);

            await delivery.NackAsync(requeue: true, stoppingToken);
        }
    }

    /// <summary>
    /// Processes a failed job message and stores it in database.
    /// </summary>
    private async Task ProcessFailedOccurrenceAsync(IMessageDelivery ea, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        _logger.Debug("Processing failed job from DLQ. MessageId: {MessageId}", ea.MessageId);

        // Deserialize the dead letter body (the job with its exception)
        var message = JsonSerializer.Deserialize<DlqJobMessage>(ea.Body.Span, ConstantJsonOptions.PropNameCaseInsensitive);

        if (message == null)
        {
            _logger.Debug("[DLQ] Failed to deserialize DLQ message as ScheduledJob.");
            return;
        }

        // Extract CorrelationId from message headers (set by the worker)
        var correlationId = ExtractCorrelationId(ea);

        if (correlationId == Guid.Empty)
        {
            _logger.Error("[DLQ] CorrelationId not found in message headers. JobId: {JobId}", message.Id);
            return;
        }

        // Extract MaxRetries from message headers (set by worker)
        int maxRetries = ExtractMaxRetries(ea);

        // Extract retry count from message headers (set by worker during retries)
        int messageRetryCount = ExtractRetryCount(ea);

        _logger.Debug("[DLQ] Processing: JobId={JobId}, CorrelationId={CorrelationId}, MessageRetryCount={RetryCount}, MaxRetries={MaxRetries}",
                      message.Id, correlationId, messageRetryCount, maxRetries);

        await using var scope = _serviceProvider.CreateAsyncScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<MilvaionDbContext>();

        // Find occurrence by CorrelationId (primary key for occurrences)
        var occurrence = await dbContext.JobOccurrences
                                        .AsNoTracking()
                                        .Select(JobOccurrence.Projections.AddFailedOccurrence)
                                        .FirstOrDefaultAsync(o => o.Id == correlationId, cancellationToken);

        if (occurrence == null)
        {
            _logger.Error("[DLQ] Occurrence not found for CorrelationId: {CorrelationId}, JobId: {JobId}", correlationId, message.Id);
            return;
        }

        _logger.Debug("[DLQ] Found occurrence: Id={Id}, Status={Status}, Exception={Exception}",
                      occurrence.Id,
                      message.Status,
                      message.Exception?[..Math.Min(100, message.Exception?.Length ?? 0)]);

        var failureType = DetermineFailureType(message.Status, message.Exception, messageRetryCount, maxRetries);

        // The worker dead-letters the job as it received it - with secrets resolved. Store the template the API
        // dispatched instead, and mask whatever the worker wrote into the exception.
        var jobData = await GetUnresolvedJobDataAsync(correlationId, message.JobData, cancellationToken);

        // Truncate exception for storage
        var truncatedException = TruncateException(_secretMasker?.Mask(message.Exception) ?? message.Exception);

        // A redelivery (the ack was lost) must not store the failure twice.
        if (await dbContext.FailedOccurrences.AsNoTracking().AnyAsync(f => f.OccurrenceId == occurrence.Id && f.RetryCount == messageRetryCount, cancellationToken))
        {
            _logger.Debug("[DLQ] Failure already stored for occurrence {OccurrenceId} (RetryCount: {RetryCount}). Skipping.", occurrence.Id, messageRetryCount);
            return;
        }

        // Create FailedOccurrence record
        var failedJob = new FailedOccurrence
        {
            Id = Guid.CreateVersion7(),
            JobId = message.Id,
            OccurrenceId = occurrence.Id,
            CorrelationId = correlationId,
            JobDisplayName = message.DisplayName,
            JobNameInWorker = message.JobNameInWorker,
            WorkerId = occurrence.WorkerId,
            JobData = NormalizeJobData(jobData),
            Exception = truncatedException,
            FailedAt = DateTime.UtcNow,
            RetryCount = messageRetryCount,
            FailureType = failureType,
            OriginalExecuteAt = occurrence.StartTime ?? occurrence.CreatedAt,
            Resolved = false
        };

        dbContext.FailedOccurrences.Add(failedJob);

        var saved = await dbContext.SaveChangesAsync(cancellationToken);

        // Record metrics
        _metrics.RecordFailedOccurrencesProcessed(1);
        _metrics.RecordFailedOccurrenceProcessDuration(sw.Elapsed.TotalMilliseconds);

        _logger.Debug("[DLQ] Failed job stored in database ({SavedCount} rows). JobId: {JobId}, OccurrenceId: {OccurrenceId}, FailureType: {FailureType}, RetryCount: {RetryCount}", saved, failedJob.JobId, failedJob.OccurrenceId, failedJob.FailureType, failedJob.RetryCount);

        // Send failed occurrence alert
        _alertNotifier.SendFireAndForget(AlertType.FailedOccurrenceReceived, new AlertPayload
        {
            Title = "Failed Occurrence (DLQ)",
            Message = $"Job '{failedJob.JobDisplayName ?? failedJob.JobNameInWorker}' exhausted all retries ({failedJob.RetryCount}). Error: {truncatedException?[..Math.Min(150, truncatedException?.Length ?? 0)]}",
            Severity = AlertSeverity.Error,
            Source = nameof(FailedOccurrenceHandler),
            ThreadKey = $"failed-occurrence-{failedJob.JobId}",
            ActionLink = $"/failed-executions",
            AdditionalData = new
            {
                failedJob.JobId,
                JobName = failedJob.JobDisplayName ?? failedJob.JobNameInWorker,
                failedJob.OccurrenceId,
                failedJob.CorrelationId,
                failedJob.RetryCount,
                failedJob.FailureType,
                failedJob.WorkerId
            }
        });
    }

    /// <summary>
    /// The job data to store for a dead-lettered occurrence: the template saved at dispatch when there is one,
    /// otherwise the worker's copy with every known secret value masked.
    /// </summary>
    private async Task<string> GetUnresolvedJobDataAsync(Guid correlationId, string workerJobData, CancellationToken cancellationToken)
    {
        if (_secretTemplateStore != null)
        {
            var template = await _secretTemplateStore.GetAsync(correlationId, cancellationToken);

            if (template != null)
                return template;
        }

        return _secretMasker?.Mask(workerJobData) ?? workerJobData;
    }

    /// <summary>
    /// The JobData column is jsonb, which rejects anything that is not JSON. Jobs without data arrive with an empty string,
    /// so empty becomes null and any other non-JSON text is stored as a JSON string.
    /// </summary>
    internal static string NormalizeJobData(string jobData)
    {
        if (string.IsNullOrWhiteSpace(jobData))
            return null;

        try
        {
            using var _ = JsonDocument.Parse(jobData);

            return jobData;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(jobData);
        }
    }

    /// <summary>
    /// Extracts OccurrenceId from the message headers.
    /// </summary>
    private static Guid ExtractCorrelationId(IMessageDelivery ea)
    {
        // Check OccurrenceId header first (new naming)
        if (ea.Headers?.ContainsKey("OccurrenceId") == true)
            return Guid.Parse(MessageHeaderValues.GetString(ea.Headers, "OccurrenceId"));

        // Fallback to CorrelationId property (contains OccurrenceId value)
        if (!string.IsNullOrEmpty(ea.CorrelationId))
            return Guid.Parse(ea.CorrelationId);

        return Guid.Empty;
    }

    /// <summary>
    /// Extracts MaxRetries from the message headers.
    /// </summary>
    private static int ExtractMaxRetries(IMessageDelivery ea)
    {
        if (ea.Headers?.ContainsKey("MaxRetries") == true)
            return MessageHeaderValues.GetInt(ea.Headers, "MaxRetries") ?? throw new FormatException("The MaxRetries header is not a number.");

        // Default fallback
        return 3;
    }

    /// <summary>
    /// Extracts retry count from the message headers.
    /// Returns the number of retry attempts that were made before moving to DLQ.
    /// </summary>
    private static int ExtractRetryCount(IMessageDelivery ea)
    {
        if (ea.Headers?.ContainsKey("x-retry-count") == true)
            return MessageHeaderValues.GetInt(ea.Headers, "x-retry-count") ?? throw new FormatException("The x-retry-count header is not a number.");

        return 0; // Default - no retries (original message)
    }

    /// <summary>
    /// Truncates exception string to prevent database bloat while preserving important context.
    /// </summary>
    /// <param name="exception">Original exception string (may be null or very large)</param>
    /// <returns>Truncated exception string (max 2KB) with marker if truncated</returns>
    private static string TruncateException(string exception)
    {
        // Return generic message if exception is null or empty
        // (e.g., routing mismatch, worker crash, TTL expire, or worker never consumed message)
        if (string.IsNullOrEmpty(exception))
            return "Job failed to complete (no exception recorded - possible routing issue, worker crash, worker capacity full or message TTL expiry)";

        // Return as-is if within limit
        if (exception.Length <= _maxExceptionLength)
            return exception;

        // Smart truncation: Try to preserve important parts of stack trace
        const string truncationMarker = "\n\n... [Exception truncated - original size: {0} chars, showing first {1} chars] ...";

        var markerLength = string.Format(truncationMarker, exception.Length, _maxExceptionLength).Length;
        var keepLength = _maxExceptionLength - markerLength;

        // Try to find last newline before truncation point to keep stack trace readable
        var lastNewLine = exception.LastIndexOf('\n', Math.Min(keepLength, exception.Length - 1));

        // If newline is found in the first half, use it; otherwise just cut at keepLength
        var truncateAt = (lastNewLine > keepLength / 2) ? lastNewLine : keepLength;

        return exception[..truncateAt] + string.Format(truncationMarker, exception.Length, truncateAt);
    }

    /// <summary>
    /// Determines the failure type based on occurrence status and exception content.
    /// </summary>
    private static FailureType DetermineFailureType(JobOccurrenceStatus status, string exception, int retryCount, int maxRetries)
    {
        if (retryCount > 0 && retryCount >= maxRetries)
            return FailureType.MaxRetriesExceeded;

        // Priority 2: Check specific status-based failures
        var failureType = status switch
        {
            JobOccurrenceStatus.TimedOut => FailureType.Timeout,
            JobOccurrenceStatus.Cancelled => FailureType.Cancelled,
            JobOccurrenceStatus.Unknown => FailureType.WorkerCrash,
            JobOccurrenceStatus.Failed when exception?.Contains("zombie", StringComparison.OrdinalIgnoreCase) == true => FailureType.ZombieDetection,
            JobOccurrenceStatus.Failed => FailureType.UnhandledException, // Generic failure
            _ => FailureType.UnhandledException // Default
        };

        return failureType;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("FailedOccurrenceHandler stopping...");

        await base.StopAsync(cancellationToken);

        if (_consumer != null)
            await _consumer.DisposeAsync();

        _logger.Information("FailedOccurrenceHandler stopped");
    }
}
