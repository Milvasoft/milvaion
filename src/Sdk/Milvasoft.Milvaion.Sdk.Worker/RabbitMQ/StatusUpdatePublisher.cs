using Microsoft.Extensions.Logging;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Models;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using System.Text;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Interface for publishing job status updates.
/// </summary>
public interface IStatusUpdatePublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes a status update to RabbitMQ.
    /// </summary>
    Task PublishStatusAsync(Guid occurrenceId,
                            Guid jobId,
                            string workerId,
                            string instanceId,
                            JobOccurrenceStatus status,
                            DateTime? startTime = null,
                            DateTime? endTime = null,
                            long? durationMs = null,
                            string result = null,
                            string exception = null,
                            CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a shutdown heartbeat to notify server that worker is stopping gracefully.
    /// Server will immediately cleanup consumer counts and running jobs.
    /// </summary>
    Task PublishShutdownHeartbeatAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Publishes job status updates to RabbitMQ for collection by producer.
/// </summary>
public class StatusUpdatePublisher(WorkerOptions options, ILoggerFactory loggerFactory) : IStatusUpdatePublisher
{
    private readonly WorkerOptions _options = options;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<StatusUpdatePublisher>();
    private readonly ResilientPublisherChannel _channel = new(options, loggerFactory.CreateMilvaLogger<StatusUpdatePublisher>(), WorkerConstant.Queues.StatusUpdates);

    public async Task PublishStatusAsync(Guid occurrenceId,
                                         Guid jobId,
                                         string workerId,
                                         string instanceId,
                                         JobOccurrenceStatus status,
                                         DateTime? startTime = null,
                                         DateTime? endTime = null,
                                         long? durationMs = null,
                                         string result = null,
                                         string exception = null,
                                         CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new JobStatusUpdateMessage
            {
                OccurrenceId = occurrenceId,
                JobId = jobId,
                WorkerId = workerId,
                InstanceId = instanceId,
                Status = status,
                StartTime = startTime,
                EndTime = endTime,
                DurationMs = durationMs,
                Result = result,
                Exception = exception,
                MessageTimestamp = DateTime.UtcNow
            };

            var json = JsonSerializer.Serialize(message);
            var body = Encoding.UTF8.GetBytes(json);

            await _channel.PublishAsync(WorkerConstant.Queues.StatusUpdates, body, persistent: true, cancellationToken);

            _logger?.Debug("Published status update: {Status} for OccurrenceId: {OccurrenceId}", status, occurrenceId);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "Failed to publish status update for OccurrenceId {OccurrenceId}, Status: {Status}", occurrenceId, status);
            throw; // Re-throw so OutboxService can store locally
        }
    }

    public async Task PublishShutdownHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var heartbeat = new WorkerHeartbeatMessage
            {
                WorkerId = _options.WorkerId,
                InstanceId = _options.InstanceId,
                CurrentJobs = 0,
                Timestamp = DateTime.UtcNow,
                IsStopping = true  // Shutdown flag
            };

            var json = JsonSerializer.Serialize(heartbeat);
            var body = Encoding.UTF8.GetBytes(json);

            await _channel.PublishAsync(WorkerConstant.Queues.WorkerHeartbeat, body, persistent: false, cancellationToken);

            _logger?.Information("Published shutdown heartbeat for {InstanceId}", _options.InstanceId);
        }
        catch (Exception ex)
        {
            _logger?.Warning(ex, "Failed to publish shutdown heartbeat for {InstanceId}", _options.InstanceId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _channel.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
