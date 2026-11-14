using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Application.Interfaces.Secrets;
using Milvaion.Infrastructure.Services.Messaging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using System.Globalization;
using System.Text;

namespace Milvaion.Infrastructure.Services.Kafka;

/// <summary>
/// Dispatches jobs to <c>milvaion.jobs.{worker}</c>, keyed by job id.
/// </summary>
public sealed class KafkaJobPublisher : IJobPublisher
{
    private readonly KafkaClientFactory _clientFactory;
    private readonly KafkaLagReader _lagReader;
    private readonly JobMessageBodyBuilder _bodyBuilder;
    private readonly IMilvaLogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="KafkaJobPublisher"/> class.
    /// </summary>
    public KafkaJobPublisher(KafkaClientFactory clientFactory,
                             KafkaLagReader lagReader,
                             ILoggerFactory loggerFactory,
                             ISecretResolver secretResolver = null,
                             ISecretTemplateStore templateStore = null)
    {
        _clientFactory = clientFactory;
        _lagReader = lagReader;
        _bodyBuilder = new JobMessageBodyBuilder(secretResolver, templateStore);
        _logger = loggerFactory.CreateMilvaLogger<KafkaJobPublisher>();
    }

    /// <inheritdoc/>
    public async Task<bool> PublishJobAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        // Outside the try below on purpose: a missing secret is not a broker problem and must reach the caller.
        var body = await _bodyBuilder.BuildAsync(job, occurrenceId, cancellationToken);

        try
        {
            var workerId = KafkaTopicNames.ResolveWorkerId(job.WorkerId, job.RoutingPattern);

            if (workerId == null)
            {
                _logger.Warning("Job {JobId} ({JobType}) has neither a worker id nor a routing pattern to derive one from. It cannot be dispatched on Kafka.", job.Id, job.JobNameInWorker);

                return false;
            }

            var topic = KafkaTopicNames.Jobs(_clientFactory.Prefix, workerId);

            // The worker creates its topic at startup. Without it, no worker has ever listened: the Kafka
            // equivalent of a RabbitMQ basic.return.
            if (!_clientFactory.TopicExists(topic))
            {
                _logger.Warning("Job {JobId} ({JobType}) could not be routed: Kafka topic '{Topic}' does not exist. Worker '{WorkerId}' may never have started.", job.Id, job.JobNameInWorker, topic, workerId);

                return false;
            }

            var occurrence = occurrenceId.ToString();
            var headers = new Headers
            {
                { KafkaHeaderNames.OccurrenceId, Encoding.UTF8.GetBytes(occurrence) },
                { KafkaHeaderNames.CorrelationId, Encoding.UTF8.GetBytes(occurrence) },
                { KafkaHeaderNames.JobType, Encoding.UTF8.GetBytes(job.JobNameInWorker ?? string.Empty) },
                { KafkaHeaderNames.WorkerId, Encoding.UTF8.GetBytes(workerId) },
                { KafkaHeaderNames.ExecuteAt, Encoding.UTF8.GetBytes(job.ExecuteAt.ToString("O", CultureInfo.InvariantCulture)) },
                { KafkaHeaderNames.Priority, Encoding.UTF8.GetBytes(Math.Clamp(job.Priority, ScheduledJob.MinPriority, ScheduledJob.MaxPriority).ToString(CultureInfo.InvariantCulture)) }
            };

            var result = await _clientFactory.Producer.ProduceAsync(topic, new Message<string, byte[]>
            {
                Key = job.Id.ToString(),
                Value = body,
                Headers = headers
            }, cancellationToken);

            _logger.Debug("Job {JobId} ({JobType}) published to Kafka {Offset} with OccurrenceId {OccurrenceId}", job.Id, job.JobNameInWorker, result.TopicPartitionOffset, occurrenceId);

            return result.Status == PersistenceStatus.Persisted;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to publish job {JobId} with OccurrenceId {OccurrenceId} to Kafka", job.Id, occurrenceId);

            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<int> PublishBatchAsync(Dictionary<ScheduledJob, Guid> jobsWithOccurrence, CancellationToken cancellationToken = default)
    {
        if (jobsWithOccurrence == null || jobsWithOccurrence.Count == 0)
            return 0;

        var publishedCount = 0;

        foreach (var (job, occurrenceId) in jobsWithOccurrence)
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

        _logger.Debug("Published {PublishedCount}/{TotalCount} jobs to Kafka", publishedCount, jobsWithOccurrence.Count);

        return publishedCount;
    }

    /// <inheritdoc/>
    public async Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default)
    {
        try
        {
            var workerId = KafkaTopicNames.ResolveWorkerId(null, routingPattern);

            if (workerId == null)
                return 0;

            var lag = await _lagReader.GetLagAsync(KafkaTopicNames.Jobs(_clientFactory.Prefix, workerId),
                                                   KafkaTopicNames.WorkerGroup(_clientFactory.Prefix, workerId),
                                                   cancellationToken);

            return lag == null ? 0 : (uint)Math.Min(lag.Lag, uint.MaxValue);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read Kafka lag for routing pattern {RoutingPattern}", routingPattern);

            return 0;
        }
    }
}
