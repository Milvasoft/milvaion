using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using RabbitMQ.Client;

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
    /// Force immediate health check (async).
    /// </summary>
    Task<bool> RefreshStatusAsync();

    /// <summary>
    /// Called when connection is restored.
    /// </summary>
    void OnConnectionRestored();
}

/// <summary>
/// Monitors connection health to RabbitMQ via periodic background checks.
/// </summary>
public class ConnectionMonitor : IConnectionMonitor
{
    private readonly IMilvaLogger _logger;
    private readonly WorkerOptions _options;
    private IConnection _rabbitConnection;
    private readonly Lock _lockObj = new();
    private bool _isHealthy = false;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _backgroundCheckTask;

    public ConnectionMonitor(WorkerOptions options, IMilvaLogger logger)
    {
        _logger = logger;
        _options = options;

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
                return _isHealthy;
        }
    }

    /// <summary>
    /// Background loop that periodically checks RabbitMQ health.
    /// </summary>
    private async Task BackgroundHealthCheckLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var isHealthy = await CheckRabbitMQHealthAsync();

                lock (_lockObj)
                {
                    _isHealthy = isHealthy;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
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
                    _isHealthy = false;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    /// <summary>
    /// Perform actual RabbitMQ health check.
    /// </summary>
    private async Task<bool> CheckRabbitMQHealthAsync()
    {
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
    /// Force immediate health check (async).
    /// </summary>
    public async Task<bool> RefreshStatusAsync()
    {
        var isHealthy = await CheckRabbitMQHealthAsync();

        lock (_lockObj)
        {
            _isHealthy = isHealthy;
        }

        return isHealthy;
    }

    public void OnConnectionRestored()
    {
        if (_rabbitConnection != null)
        {
            _rabbitConnection.CallbackExceptionAsync += (sender, args) =>
            {
                _logger?.Warning(args.Exception, "RabbitMQ callback exception");
                return Task.CompletedTask;
            };
        }
    }

    private async Task DisposeConnectionAsync()
    {
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
        _cts?.Cancel();
        _backgroundCheckTask?.GetAwaiter().GetResult();
        DisposeConnectionAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_backgroundCheckTask != null)
            await _backgroundCheckTask;
        await DisposeConnectionAsync();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}