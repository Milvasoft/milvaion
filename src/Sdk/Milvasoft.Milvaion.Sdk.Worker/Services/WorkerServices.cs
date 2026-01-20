using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using StackExchange.Redis;

namespace Milvasoft.Milvaion.Sdk.Worker.Services;

/// <summary>
/// Listens to Redis Pub/Sub for job cancellation signals.
/// </summary>
public class CancellationListener(IOptions<WorkerOptions> options, ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<IMilvaLogger>();
    private ConnectionMultiplexer _redis;
    private readonly Dictionary<Guid, CancellationTokenSource> _activeCancellations = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger?.Information("Cancellation listener starting");

        _redis = await ConnectionMultiplexer.ConnectAsync(_options.Redis.ConnectionString);

        var subscriber = _redis.GetSubscriber();

        await subscriber.SubscribeAsync(new RedisChannel(_options.Redis.CancellationChannel, RedisChannel.PatternMode.Literal), (channel, message) =>
        {
            if (Guid.TryParse(message.ToString(), out var jobId))
            {
                _logger?.Debug("Received cancellation signal for job {JobId}", jobId);

                // Trigger cancellation if we have an active CTS for this job
                lock (_activeCancellations)
                {
                    if (_activeCancellations.TryGetValue(jobId, out var cts))
                    {
                        cts.Cancel();
                        _activeCancellations.Remove(jobId);

                        _logger?.Debug("Cancelled job {JobId}", jobId);
                    }
                }
            }
        });

        _logger?.Information("Subscribed to cancellation channel: {Channel}", _options.Redis.CancellationChannel);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    public void RegisterCancellation(Guid jobId, CancellationTokenSource cts)
    {
        lock (_activeCancellations)
        {
            _activeCancellations[jobId] = cts;
        }
    }

    public void UnregisterCancellation(Guid jobId)
    {
        lock (_activeCancellations)
        {
            _activeCancellations.Remove(jobId);
        }
    }

    public override void Dispose()
    {
        _redis?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
