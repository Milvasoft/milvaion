using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Secrets;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.RabbitMQ;

/// <summary>
/// RabbitMQ publisher implementation for job dispatching.
/// </summary>
public class RabbitMQPublisher : IRabbitMQPublisher
{
    private readonly RabbitMQConnectionFactory _connectionFactory;
    private readonly IMilvaLogger _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ISecretResolver _secretResolver;
    private readonly ISecretTemplateStore _templateStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQPublisher"/> class.
    /// </summary>
    public RabbitMQPublisher(RabbitMQConnectionFactory connectionFactory,
                             ILoggerFactory loggerFactory,
                             ISecretResolver secretResolver = null,
                             ISecretTemplateStore templateStore = null)
    {
        _connectionFactory = connectionFactory;
        _secretResolver = secretResolver;
        _templateStore = templateStore;
        _logger = loggerFactory.CreateMilvaLogger<RabbitMQPublisher>();
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc/>
    public async Task<bool> PublishJobAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        // Outside the try below on purpose: a missing secret is not a broker problem, so it must not turn into
        // "return false" (which callers retry) but reach the caller, which fails the occurrence.
        var body = await BuildMessageBodyAsync(job, occurrenceId, cancellationToken);

        try
        {
            // Publisher confirms enabled: BasicPublishAsync below only completes once RabbitMQ has acked the message.
            await using var channel = await _connectionFactory.CreateChannelAsync(enablePublisherConfirms: true, cancellationToken);

            // Determine routing key:
            // 1. If job has specific routing patterns (from worker), use first one
            // 2. Otherwise, generate from JobType
            string routingKey;

            if (!string.IsNullOrWhiteSpace(job.RoutingPattern))
            {
                // Use first routing pattern from worker
                // Example: worker has ["test.*", "send.email.*"] ? use "test.job" pattern
                var pattern = job.RoutingPattern;

                routingKey = pattern.Replace("*", "job").Replace("..", "."); // "test.*" → "test.job"

                _logger.Debug("Using worker routing pattern '{Pattern}' → routing key '{RoutingKey}' for job {JobId}", pattern, routingKey, job.Id);
            }
            else
            {
                // Fallback: Generate routing key from JobType + WorkerId
                // This ensures worker-specific routing even without explicit RoutingPattern
                routingKey = GetRoutingKeyFromJobType(job.JobNameInWorker, job.WorkerId);

                _logger.Debug("Generated routing key '{RoutingKey}' from JobType '{JobType}' and WorkerId '{WorkerId}' for job {JobId}", routingKey, job.JobNameInWorker, job.WorkerId ?? "null", job.Id);
            }

            // Create message properties
            var properties = new BasicProperties
            {
                Persistent = true, // Survive broker restart
                ContentType = "application/json",
                MessageId = job.Id.ToString(),
                CorrelationId = occurrenceId.ToString(), // Unique occurrence ID sent to worker
                Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                // AMQP priority 0-9. Worker queues are not declared with x-max-priority yet, so today the broker
                // ignores it; ordering comes from the dispatcher sending higher priority jobs first.
                Priority = (byte)Math.Clamp(job.Priority, ScheduledJob.MinPriority, ScheduledJob.MaxPriority),
                Headers = new Dictionary<string, object>
                {
                    ["JobType"] = job.JobNameInWorker,
                    ["ExecuteAt"] = job.ExecuteAt.ToString("O"),
                    ["IsRecurring"] = job.CronExpression != null,
                    ["OccurrenceId"] = occurrenceId.ToString(),
                    ["RoutingKey"] = routingKey,
                    ["WorkerId"] = job.WorkerId ?? "any"
                }
            };

            // Detect mandatory returns: RabbitMQ fires BasicReturn when no queue binding matches
            // the routing key (i.e. the target worker is offline or its queue was never declared).
            var returnTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            channel.BasicReturnAsync += (_, args) =>
            {
                returnTcs.TrySetResult(args.ReplyText ?? "No route");
                return Task.CompletedTask;
            };

            // Publish to TOPIC EXCHANGE with routing key
            await channel.BasicPublishAsync(exchange: WorkerConstant.ExchangeName,
                                            routingKey: routingKey,
                                            mandatory: true,
                                            basicProperties: properties,
                                            body: body,
                                            cancellationToken: cancellationToken);

            // With confirms, the broker sends basic.return before basic.ack, so once the publish above has
            // returned the answer is already known. Without confirms nothing marks the end of the exchange,
            // so the broker gets a moment to return the message.
            if (!_connectionFactory.PublisherConfirmsEnabled)
                await Task.WhenAny(returnTcs.Task, Task.Delay(500, cancellationToken));

            var returnedReason = returnTcs.Task.IsCompleted ? await returnTcs.Task : null;

            if (returnedReason != null)
            {
                _logger.Warning("Job {JobId} ({JobType}) could not be routed — no worker queue bound for routing key '{RoutingKey}'. Reason: {Reason}. Worker '{WorkerId}' may be offline.", job.Id, job.JobNameInWorker, routingKey, returnedReason, job.WorkerId ?? "any");

                return false;
            }

            _logger.Debug("Job {JobId} ({JobType}) published to exchange {ExchangeName} with routing key '{RoutingKey}' and OccurrenceId {OccurrenceId}", job.Id, job.JobNameInWorker, WorkerConstant.ExchangeName, routingKey, occurrenceId);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to publish job {JobId} with OccurrenceId {OccurrenceId} to RabbitMQ", job.Id, occurrenceId);

            return false;
        }
    }

    /// <summary>
    /// Serializes the job for the worker, with secret references in its job data resolved.
    /// </summary>
    /// <remarks>
    /// The job object itself is never changed: it may be the cached or tracked instance, and anything written
    /// to it could end up in Redis or the database. The resolved data exists only in the returned bytes.
    /// </remarks>
    /// <exception cref="SecretResolutionException">A referenced secret could not be resolved.</exception>
    private async Task<byte[]> BuildMessageBodyAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken)
    {
        if (_secretResolver == null || !SecretReferences.ContainsReference(job.JobData))
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job, _jsonOptions));

        var resolved = await _secretResolver.ResolveAsync(job.JobData, cancellationToken);

        if (_templateStore != null)
            await _templateStore.SaveAsync(occurrenceId, job.JobData, cancellationToken);

        var node = JsonSerializer.SerializeToNode(job, _jsonOptions)!.AsObject();

        node["jobData"] = resolved;

        return Encoding.UTF8.GetBytes(node.ToJsonString(_jsonOptions));
    }

    /// <summary>
    /// Converts JobType to routing key WITHOUT splitting words.
    /// Includes WorkerId prefix for worker-specific routing.
    /// Examples (with workerId "email-worker-01"):
    /// - NonParallelJob → email-worker-01.nonparallel.job
    /// - TestJob → email-worker-01.test.job
    /// - SendEmailJob → email-worker-01.sendemail.job
    /// </summary>
    private static string GetRoutingKeyFromJobType(string jobType, string workerId)
    {
        // Remove "Job" suffix if exists
        var normalized = jobType.EndsWith("Job", StringComparison.OrdinalIgnoreCase) ? jobType[..^3] : jobType;

        // Convert to lowercase WITHOUT splitting by uppercase
        // "NonParallel" → "nonparallel"
        // "SendEmail" → "sendemail"
        var routingKey = normalized.ToLowerInvariant();

        // Include WorkerId prefix for worker-specific routing
        // This ensures different workers with same job names don't receive each other's jobs
        if (!string.IsNullOrEmpty(workerId))
        {
            // "email-worker-01" + "sendemail" → "email-worker-01.sendemail.job"
            return $"{workerId.ToLowerInvariant()}.{routingKey}.job";
        }

        // Fallback without workerId (should not happen in normal flow)
        return $"{routingKey}.job";
    }

    /// <inheritdoc/>
    public async Task<int> PublishBatchAsync(Dictionary<ScheduledJob, Guid> jobsWithCorrelation, CancellationToken cancellationToken = default)
    {
        if (jobsWithCorrelation.IsNullOrEmpty())
            return 0;

        try
        {
            var publishedCount = 0;

            foreach (var (job, occurrenceId) in jobsWithCorrelation)
            {
                try
                {
                    if (await PublishJobAsync(job, occurrenceId, cancellationToken))
                        publishedCount++;
                }
                catch (SecretResolutionException ex)
                {
                    _logger.Warning("Job {JobId} was not published: {Reason}", job.Id, ex.Message);
                }
            }

            _logger.Debug("Published {PublishedCount}/{TotalCount} jobs to RabbitMQ", publishedCount, jobsWithCorrelation.Count);

            return publishedCount;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to publish job batch to RabbitMQ");
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await _connectionFactory.CreateChannelAsync(cancellationToken);

            // If routingPatterns provided, use them. Otherwise fall back to default "all" queue.
            string queueName;

            if (!string.IsNullOrEmpty(routingPattern))
            {
                // Worker-specific queue name generation (matches JobConsumer.cs logic)
                var queueSuffix = routingPattern.Replace("*", "wildcard").Replace("#", "all");

                queueName = $"{WorkerConstant.Queues.Jobs}.{queueSuffix}";
            }
            else
            {
                // Default queue (all workers with "#" pattern)
                queueName = $"{WorkerConstant.Queues.Jobs}.all";
            }

            try
            {
                // QueueDeclarePassive - check if queue exists and get message count
                var queueInfo = await channel.QueueDeclarePassiveAsync(queueName, cancellationToken);

                _logger.Debug("Queue {QueueName} has {MessageCount}", queueName, queueInfo.MessageCount);

                return queueInfo.MessageCount;
            }
            catch (Exception ex)
            {
                // Queue doesn't exist - this is OK if worker not running yet
                _logger.Debug(ex, "Queue {QueueName} not found, returning 0", queueName);

                return 0;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to get queue message count for job type");

            return 0; // Return 0 on error (safe default)
        }
    }
}
