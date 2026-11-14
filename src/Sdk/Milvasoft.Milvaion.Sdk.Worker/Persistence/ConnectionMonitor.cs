using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using RabbitMQ.Client;
using StackExchange.Redis;

namespace Milvasoft.Milvaion.Sdk.Worker.Persistence;

/// <summary>
/// Interface for connection health monitoring.
/// </summary>
public interface IConnectionMonitor : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Current RabbitMQ connection health status.
    /// </summary>
    bool IsRabbitMQHealthy { get; }

    /// <summary>
    /// Current Redis connection health status.
    /// </summary>
    bool IsRedisHealthy { get; }

    /// <summary>
    /// Force immediate health check for all connections (async).
    /// </summary>
    Task<bool> RefreshStatusAsync();

    /// <summary>
    /// Called when connection is restored.
    /// </summary>
    void OnConnectionRestored();
}

/// <summary>
/// Monitors connection health to RabbitMQ and Redis via periodic background checks.
/// Uses exponential backoff with jitter to prevent thundering herd when services are recovering.
/// </summary>
public class ConnectionMonitor : IConnectionMonitor
{
    private readonly IMilvaLogger _logger;
    private readonly WorkerOptions _options;
    private readonly IConnectionMultiplexer _redis;
    private IConnection _rabbitConnection;
    private Confluent.Kafka.IAdminClient _kafkaAdmin;
    private readonly Lock _lockObj = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backgroundCheckTask;
    private bool _disposed = false;

    // Backoff configuration
    private const double _healthyIntervalSeconds = 30;
    private const double _initialBackoffSeconds = 5;
    private const double _maxBackoffSeconds = 60;
    private const double _backoffMultiplier = 2;
    private const double _jitterFactor = 0.2; // ±20%

    // RabbitMQ backoff state
    // Optimistic until the first check: a publish that fails still falls back to the local store,
    // whereas assuming unhealthy delays every status of the first seconds by a whole sync interval.
    private bool _isRabbitHealthy = true;
    private double _rabbitBackoffSeconds = _initialBackoffSeconds;
    private int _rabbitConsecutiveFailures;
    private bool _wasRabbitHealthy = true;

    // Redis backoff state
    private bool _isRedisHealthy = false;
    private bool _wasRedisHealthy = true;

    private static readonly Random _jitterRandom = new();

    public ConnectionMonitor(WorkerOptions options, IMilvaLogger logger, IConnectionMultiplexer redis = null)
    {
        _logger = logger;
        _options = options;
        _redis = redis;

        // Start background health check loop
        _backgroundCheckTask = Task.Run(() => BackgroundHealthCheckLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Current RabbitMQ connection health status (updated by background task).
    /// </summary>
    public bool IsRabbitMQHealthy
    {
        get
        {
            lock (_lockObj)
                return _isRabbitHealthy;
        }
    }

    /// <summary>
    /// Current Redis connection health status (updated by background task).
    /// Returns true if Redis is not configured.
    /// </summary>
    public bool IsRedisHealthy
    {
        get
        {
            lock (_lockObj)
                return _isRedisHealthy;
        }
    }

    /// <summary>
    /// Background loop that periodically checks RabbitMQ and Redis health.
    /// Healthy: checks every 30s. Unhealthy: exponential backoff 5s → 10s → 20s → 40s → 60s (cap) with ±20% jitter.
    /// </summary>
    private async Task BackgroundHealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var rabbitHealthy = await CheckRabbitMQHealthAsync();
                var redisHealthy = CheckRedisHealth();

                lock (_lockObj)
                {
                    _isRabbitHealthy = rabbitHealthy;
                    _isRedisHealthy = redisHealthy;
                }

                // RabbitMQ state transition logging
                if (rabbitHealthy && !_wasRabbitHealthy)
                {
                    _logger?.Information("RabbitMQ connection restored after {Failures} consecutive failure(s)", _rabbitConsecutiveFailures);
                    _rabbitBackoffSeconds = _initialBackoffSeconds;
                    _rabbitConsecutiveFailures = 0;
                }
                else if (!rabbitHealthy && _wasRabbitHealthy)
                {
                    _logger?.Warning("RabbitMQ connection lost. Entering backoff retry...");
                }

                // Redis state transition logging
                if (redisHealthy && !_wasRedisHealthy)
                {
                    _logger?.Information("Redis connection restored");
                }
                else if (!redisHealthy && _wasRedisHealthy)
                {
                    _logger?.Warning("Redis connection lost");
                }

                _wasRabbitHealthy = rabbitHealthy;
                _wasRedisHealthy = redisHealthy;

                // Use the shorter delay between RabbitMQ backoff and healthy interval
                var allHealthy = rabbitHealthy && redisHealthy;

                var delay = allHealthy
                    ? TimeSpan.FromSeconds(_healthyIntervalSeconds)
                    : TimeSpan.FromSeconds(ApplyJitter(_rabbitBackoffSeconds));

                if (!rabbitHealthy)
                {
                    _rabbitConsecutiveFailures++;
                    _rabbitBackoffSeconds = Math.Min(_rabbitBackoffSeconds * _backoffMultiplier, _maxBackoffSeconds);
                }

                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Background health check loop error");

                lock (_lockObj)
                {
                    _isRabbitHealthy = false;
                    _isRedisHealthy = _redis?.IsConnected ?? true;
                }

                _rabbitConsecutiveFailures++;
                var delay = TimeSpan.FromSeconds(ApplyJitter(_rabbitBackoffSeconds));
                _rabbitBackoffSeconds = Math.Min(_rabbitBackoffSeconds * _backoffMultiplier, _maxBackoffSeconds);

                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Applies ±20% jitter to desynchronize retry attempts across pods.
    /// </summary>
    private static double ApplyJitter(double seconds)
    {
        var jitter = seconds * _jitterFactor * (2 * _jitterRandom.NextDouble() - 1);
        return Math.Max(1, seconds + jitter);
    }

    /// <summary>
    /// Perform actual RabbitMQ health check.
    /// </summary>
    private async Task<bool> CheckRabbitMQHealthAsync()
    {
        // On Kafka the "RabbitMQ" state tracks the Kafka cluster: it is the message broker either way.
        if (_options.UsesKafka)
            return await CheckKafkaHealthAsync();

        try
        {
            if (_rabbitConnection != null && _rabbitConnection.IsOpen)
                return true;

            await DisposeConnectionAsync();

            var factory = new ConnectionFactory
            {
                HostName = _options.RabbitMQ.Host,
                Port = _options.RabbitMQ.Port,
                UserName = _options.RabbitMQ.Username,
                Password = _options.RabbitMQ.Password,
                VirtualHost = _options.RabbitMQ.VirtualHost,
                RequestedConnectionTimeout = TimeSpan.FromSeconds(5),
                AutomaticRecoveryEnabled = true
            };

            _rabbitConnection = await factory.CreateConnectionAsync();

            return _rabbitConnection.IsOpen;
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "RabbitMQ health check failed");
            return false;
        }
    }

    /// <summary>
    /// Kafka health: at least one broker answers a metadata request.
    /// </summary>
    private async Task<bool> CheckKafkaHealthAsync()
    {
        try
        {
            _kafkaAdmin ??= new Confluent.Kafka.AdminClientBuilder(Kafka.KafkaWorkerClients.BuildClientConfig(_options.Kafka)).Build();

            var metadata = await Task.Run(() => _kafkaAdmin.GetMetadata(TimeSpan.FromSeconds(5)));

            return metadata.Brokers.Count > 0;
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "Kafka health check failed");
            return false;
        }
    }

    /// <summary>
    /// Check Redis connection health. Returns true if Redis is not configured.
    /// </summary>
    private bool CheckRedisHealth()
    {
        if (_redis == null)
            return true;

        try
        {
            return _redis.IsConnected;
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "Redis health check failed");
            return false;
        }
    }

    /// <summary>
    /// Force immediate health check for all connections (async).
    /// </summary>
    public async Task<bool> RefreshStatusAsync()
    {
        var rabbitHealthy = await CheckRabbitMQHealthAsync();
        var redisHealthy = CheckRedisHealth();

        lock (_lockObj)
        {
            _isRabbitHealthy = rabbitHealthy;
            _isRedisHealthy = redisHealthy;
        }

        return rabbitHealthy && redisHealthy;
    }

    public void OnConnectionRestored() => _rabbitConnection?.CallbackExceptionAsync += (sender, args) =>
    {
        _logger?.Warning(args.Exception, "RabbitMQ callback exception");
        return Task.CompletedTask;
    };

    private async Task DisposeConnectionAsync()
    {
        _kafkaAdmin?.Dispose();
        _kafkaAdmin = null;

        try
        {
            if (_rabbitConnection != null)
            {
                await _rabbitConnection.CloseAsync();
                _rabbitConnection.Dispose();
                _rabbitConnection = null;
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "Error disposing RabbitMQ connection");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        try
        {
            _backgroundCheckTask?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled while backing off
        }

        DisposeConnectionAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            if (!_cts.IsCancellationRequested)
                _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed, ignore
        }

        try
        {
            if (_backgroundCheckTask != null)
                await _backgroundCheckTask;
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled while backing off
        }

        await DisposeConnectionAsync();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}