using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Background service that listens for job cancellation requests from Redis Pub/Sub.
/// </summary>
public class CancellationListenerService(IConnectionMultiplexer redis,
                                         IOptions<WorkerOptions> options,
                                         ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<CancellationListenerService>();
    private readonly WorkerOptions _options = options.Value;

    // Track active CancellationTokenSources by CorrelationId
    private static readonly ConcurrentDictionary<Guid, CancellationTokenSource> _activeCancellations = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("CancellationListenerService starting...");

        var subscriber = _redis.GetSubscriber();
        var cancellationChannel = _options.Redis.CancellationChannel;

        await subscriber.SubscribeAsync(RedisChannel.Literal(_options.Redis.CancellationChannel), (channel, message) =>
        {
            try
            {
                var messageString = message.ToString();
                var cancellationRequest = JsonSerializer.Deserialize<CancellationRequest>(messageString);

                if (cancellationRequest == null)
                {
                    _logger.Debug("Failed to deserialize cancellation request");
                    return;
                }

                var correlationId = Guid.Parse(cancellationRequest.CorrelationId);

                if (_activeCancellations.TryGetValue(correlationId, out var cts))
                {
                    _logger.Debug("Cancelling job {CorrelationId}: {Reason}", correlationId, cancellationRequest.Reason);

                    cts.Cancel();

                    // Remove from tracking after cancellation
                    _activeCancellations.TryRemove(correlationId, out _);
                }
                else
                {
                    _logger.Debug("Cancellation request for {CorrelationId} but job not running on this worker", correlationId);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing cancellation request");
            }
        });

        _logger.Information("Subscribed to cancellation channel: {Channel}", cancellationChannel);

        // Keep running
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Registers a job execution with a cancellation token source.
    /// </summary>
    public static void RegisterJob(Guid correlationId, CancellationTokenSource cts) => _activeCancellations[correlationId] = cts;

    /// <summary>
    /// Unregisters a completed job.
    /// </summary>
    public static void UnregisterJob(Guid correlationId) => _activeCancellations.TryRemove(correlationId, out _);

    /// <summary>
    /// Gets the cancellation token for a job if it's registered.
    /// </summary>
    public static CancellationToken GetCancellationToken(Guid correlationId) => _activeCancellations.TryGetValue(correlationId, out var cts)
            ? cts.Token
            : CancellationToken.None;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Information("CancellationListenerService stopping...");

        var subscriber = _redis.GetSubscriber();

        await subscriber.UnsubscribeAsync(RedisChannel.Literal(_options.Redis.CancellationChannel));

        await base.StopAsync(cancellationToken);
    }

    private class CancellationRequest
    {
        public string CorrelationId { get; set; }
        public string JobId { get; set; }
        public string Reason { get; set; }
    }
}
