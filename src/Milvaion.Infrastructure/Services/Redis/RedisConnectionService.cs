using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using StackExchange.Redis;

namespace Milvaion.Infrastructure.Services.Redis;

/// <summary>
/// Manages Redis connection lifecycle for job scheduler.
/// </summary>
public class RedisConnectionService : IDisposable
{
    private readonly IMilvaLogger _logger;
    private readonly RedisOptions _options;
    private readonly Lazy<ConnectionMultiplexer> _lazyConnection;

    /// <summary>
    /// Gets the active Redis connection multiplexer.
    /// </summary>
    public IConnectionMultiplexer Connection => _lazyConnection.Value;

    /// <summary>
    /// Gets the Redis database instance.
    /// </summary>
    public IDatabase Database => Connection.GetDatabase(_options.Database);

    /// <summary>
    /// Gets the Redis subscriber for Pub/Sub operations.
    /// </summary>
    public ISubscriber Subscriber => Connection.GetSubscriber();

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisConnectionService"/> class.
    /// </summary>
    public RedisConnectionService(IOptions<RedisOptions> options, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateMilvaLogger<RedisConnectionService>();
        _options = options.Value;
        _lazyConnection = new Lazy<ConnectionMultiplexer>(() =>
        {
            try
            {
                var configOptions = ConfigurationOptions.Parse(_options.ConnectionString);

                // Connection resilience settings
                configOptions.ConnectTimeout = _options.ConnectTimeout;
                configOptions.SyncTimeout = _options.SyncTimeout;
                configOptions.AbortOnConnectFail = false; //? CRITICAL: Keep trying on failure
                configOptions.ConnectRetry = 3; // Retry 3 times before giving up
                configOptions.ReconnectRetryPolicy = new ExponentialRetry(5000); // Start with 5s, double each time

                // Keep-alive and heartbeat settings
                configOptions.KeepAlive = 60; // Send keep-alive every 60 seconds
                configOptions.DefaultVersion = new Version(6, 0); // Redis 6.0+

                // Automatic recovery settings
                configOptions.AbortOnConnectFail = false; // Continue even if initial connection fails

                if (!string.IsNullOrWhiteSpace(_options.Password))
                {
                    configOptions.Password = _options.Password;
                }

                _logger.Information("Connecting to Redis: {ConnectionString}, ConnectRetry: 3, ReconnectRetryPolicy: ExponentialRetry(5000ms)", _options.ConnectionString);

                var connection = ConnectionMultiplexer.Connect(configOptions);

                connection.ConnectionFailed += OnConnectionFailed;
                connection.ConnectionRestored += OnConnectionRestored;
                connection.ErrorMessage += OnErrorMessage;
                connection.InternalError += OnInternalError;

                _logger.Information("Redis connection established: {Endpoints}, DB: {Database}, IsConnected: {IsConnected}", string.Join(", ", connection.GetEndPoints()), _options.Database, connection.IsConnected);

                return connection;
            }
            catch (Exception ex)
            {
                _logger.Fatal(ex, "Failed to initialize Redis connection at {ConnectionString}. System will continue with degraded functionality.", _options.ConnectionString);

                //? Don't throw - let system continue with degraded functionality
                throw;
            }
        });
    }

    private void OnConnectionFailed(object sender, ConnectionFailedEventArgs e) => _logger.Error(e.Exception, "Redis connection failed: {EndPoint}, {FailureType}, ConnectionType: {ConnectionType}", e.EndPoint, e.FailureType, e.ConnectionType);

    private void OnConnectionRestored(object sender, ConnectionFailedEventArgs e) => _logger.Information("Redis connection restored: {EndPoint}, ConnectionType: {ConnectionType}", e.EndPoint, e.ConnectionType);

    private void OnErrorMessage(object sender, RedisErrorEventArgs e) => _logger.Error("Redis error: {Message}, EndPoint: {EndPoint}", e.Message, e.EndPoint);

    private void OnInternalError(object sender, InternalErrorEventArgs e) => _logger.Error(e.Exception, "Redis internal error: {Origin}, ConnectionType: {ConnectionType}", e.Origin, e.ConnectionType);

    /// <summary>
    /// Checks if Redis is connected and responsive.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!Connection.IsConnected)
                return false;

            await Database.PingAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Redis health check failed");
            return false;
        }
    }

    /// <summary>
    /// Disposes the Redis connection.
    /// </summary>
    public void Dispose()
    {
        if (_lazyConnection.IsValueCreated)
        {
            _logger.Information("Closing Redis connection");
            Connection.Close();
            Connection.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
