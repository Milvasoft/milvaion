using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Redis;
using StackExchange.Redis;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Redis-based job cancellation service implementation.
/// </summary>
public class JobCancellationService(IConnectionMultiplexer redis,
                                    IRedisCircuitBreaker circuitBreaker,
                                    IOptions<RedisOptions> options) : IJobCancellationService
{
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly IRedisCircuitBreaker _circuitBreaker = circuitBreaker;

    /// <inheritdoc/>
    public async Task<long> PublishCancellationAsync(Guid correlationId,
                                                     Guid jobId,
                                                     Guid occurrenceId,
                                                     string reason,
                                                     CancellationToken cancellationToken = default) => await _circuitBreaker.ExecuteAsync(
            operation: async () =>
            {
                var subscriber = _redis.GetSubscriber();

                var cancellationMessage = new
                {
                    CorrelationId = correlationId.ToString(),
                    JobId = jobId.ToString(),
                    OccurrenceId = occurrenceId.ToString(),
                    Reason = reason
                };

                return await subscriber.PublishAsync(RedisChannel.Literal(options.Value.CancellationChannel), JsonSerializer.Serialize(cancellationMessage));
            },
            fallback: async () => 0L,
            operationName: "PublishJobCancellation",
            cancellationToken: cancellationToken
        );
}
