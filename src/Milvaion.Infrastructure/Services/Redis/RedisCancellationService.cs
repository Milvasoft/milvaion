using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Redis Pub/Sub implementation for job cancellation.
/// </summary>
public class RedisCancellationService : IRedisCancellationService
{
    private readonly RedisConnectionService _redisConnection;
    private readonly RedisOptions _options;
    private readonly IMilvaLogger _logger;
    private readonly IRedisCircuitBreaker _circuitBreaker;
    private readonly ISubscriber _subscriber;

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisCancellationService"/> class.
    /// </summary>
    public RedisCancellationService(RedisConnectionService redisConnection,
                                    IOptions<RedisOptions> options,
                                    IRedisCircuitBreaker circuitBreaker,
                                    ILoggerFactory loggerFactory)
    {
        _redisConnection = redisConnection;
        _options = options.Value;
        _circuitBreaker = circuitBreaker;
        _logger = loggerFactory.CreateMilvaLogger<RedisCancellationService>();
        _subscriber = _redisConnection.Subscriber;
    }

    /// <inheritdoc/>
    public Task<long> PublishCancellationAsync(Guid jobId, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var channel = _options.CancellationChannel;

                    var subscriberCount = await _subscriber.PublishAsync(new RedisChannel(channel, RedisChannel.PatternMode.Literal), jobId.ToString());

                    _logger.Debug("Cancellation signal published for job {JobId} to {SubscriberCount} subscribers", jobId, subscriberCount);

                    return subscriberCount;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to publish cancellation for job {JobId}", jobId);
                    throw;
                }
            },
            fallback: async () => 0L,
            operationName: "PublishCancellation",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task SubscribeToCancellationsAsync(Action<Guid> onCancellation, CancellationToken cancellationToken = default) => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    var channel = _options.CancellationChannel;

                    await _subscriber.SubscribeAsync(new RedisChannel(channel, RedisChannel.PatternMode.Literal), (redisChannel, message) =>
                    {
                        try
                        {
                            if (Guid.TryParse(message.ToString(), out var jobId))
                            {
                                _logger.Debug("Received cancellation signal for job {JobId}", jobId);

                                onCancellation(jobId);
                            }
                            else
                            {
                                _logger.Warning("Received invalid cancellation message: {Message}", message);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error processing cancellation signal");
                        }
                    });

                    _logger.Information("Subscribed to cancellation channel: {Channel}", channel);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to subscribe to cancellations");
                    throw;
                }
            },
            fallback: async () => true,
            operationName: "SubscribeToCancellations",
            cancellationToken: cancellationToken
        );

    /// <inheritdoc/>
    public Task UnsubscribeFromCancellationsAsync() => _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                try
                {
                    await _subscriber.UnsubscribeAsync(new RedisChannel(_options.CancellationChannel, RedisChannel.PatternMode.Literal));

                    _logger.Information("Unsubscribed from cancellation channel: {Channel}", _options.CancellationChannel);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to unsubscribe from cancellations");
                    throw;
                }
            },
            fallback: async () => true,
            operationName: "UnsubscribeFromCancellations",
            cancellationToken: default
        );
}
