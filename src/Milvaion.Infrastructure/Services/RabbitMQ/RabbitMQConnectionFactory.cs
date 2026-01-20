using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Milvaion.Infrastructure.Services.RabbitMQ;

/// <summary>
/// Manages RabbitMQ connection lifecycle for job dispatcher.
/// </summary>
public class RabbitMQConnectionFactory : IDisposable
{
    private readonly IMilvaLogger _logger;
    private readonly RabbitMQOptions _options;
    private readonly Lazy<IConnection> _lazyConnection;
    private IChannel _channel;
    private readonly SemaphoreSlim _channelLock = new(1, 1);

    /// <summary>
    /// Gets the active RabbitMQ connection.
    /// </summary>
    public IConnection Connection => _lazyConnection.Value;

    /// <summary>
    /// Initializes a new instance of the <see cref="RabbitMQConnectionFactory"/> class.
    /// </summary>
    public RabbitMQConnectionFactory(IOptions<RabbitMQOptions> options, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateMilvaLogger<RabbitMQConnectionFactory>();
        _options = options.Value;
        _lazyConnection = new Lazy<IConnection>(() =>
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = _options.Host,
                    Port = _options.Port,
                    UserName = _options.Username,
                    Password = _options.Password,
                    VirtualHost = _options.VirtualHost,
                    RequestedHeartbeat = TimeSpan.FromSeconds(_options.Heartbeat),
                    AutomaticRecoveryEnabled = _options.AutomaticRecoveryEnabled,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(_options.NetworkRecoveryInterval),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(_options.ConnectionTimeout)
                };

                var connection = factory.CreateConnectionAsync().GetAwaiter().GetResult();

                connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
                connection.CallbackExceptionAsync += OnCallbackExceptionAsync;
                connection.ConnectionBlockedAsync += OnConnectionBlockedAsync;

                _logger.Information("RabbitMQ connection established: {Host}:{Port}, VirtualHost: {VirtualHost}", _options.Host, _options.Port, _options.VirtualHost);

                return connection;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to connect to RabbitMQ at {Host}:{Port}", _options.Host, _options.Port);
                throw;
            }
        });
    }

    /// <summary>
    /// Gets or creates a channel for publishing messages.
    /// </summary>
    public async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken = default)
    {
        await _channelLock.WaitAsync(cancellationToken);

        try
        {
            if (_channel == null || _channel.IsClosed)
            {
                _channel = await Connection.CreateChannelAsync(cancellationToken: cancellationToken);

                // 1. Create Dead Letter Exchange (DLX)
                await _channel.ExchangeDeclareAsync(exchange: WorkerConstant.DeadLetterExchangeName,
                                                    type: "direct",
                                                    durable: true,
                                                    autoDelete: false,
                                                    arguments: null,
                                                    cancellationToken: cancellationToken);

                // 2. Create Dead Letter Queue (DLQ)
                await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.FailedOccurrences,
                                                 durable: true,
                                                 exclusive: false,
                                                 autoDelete: false,
                                                 arguments: null,
                                                 cancellationToken: cancellationToken);

                // 3. Bind DLQ to DLX
                await _channel.QueueBindAsync(queue: WorkerConstant.Queues.FailedOccurrences,
                                              exchange: WorkerConstant.DeadLetterExchangeName,
                                              routingKey: WorkerConstant.DeadLetterRoutingKey,
                                              arguments: null,
                                              cancellationToken: cancellationToken);

                // 4. Create Main Queue with DLX settings
                var mainQueueArgs = new Dictionary<string, object>
                {
                    { "x-dead-letter-exchange", WorkerConstant.DeadLetterExchangeName },
                    { "x-dead-letter-routing-key", WorkerConstant.DeadLetterRoutingKey }
                };

                await _channel.QueueDeclareAsync(queue: WorkerConstant.Queues.Jobs,
                                                 durable: _options.Durable,
                                                 exclusive: false,
                                                 autoDelete: _options.AutoDelete,
                                                 arguments: mainQueueArgs,
                                                 cancellationToken: cancellationToken);

                _logger.Information("RabbitMQ channel created with DLQ support. Main queue: {MainQueue}, DLQ: {DLQ}, DLX: {DLX}", WorkerConstant.Queues.Jobs, WorkerConstant.Queues.FailedOccurrences, WorkerConstant.DeadLetterExchangeName);
            }

            return _channel;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    /// <summary>
    /// Checks if RabbitMQ connection is healthy.
    /// </summary>
    public bool IsHealthy()
    {
        try
        {
            return _lazyConnection.IsValueCreated && Connection.IsOpen;
        }
        catch
        {
            return false;
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        _logger.Warning("RabbitMQ connection shutdown: {ReplyCode} - {ReplyText}", e.ReplyCode, e.ReplyText);

        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs e)
    {
        _logger.Error(e.Exception, "RabbitMQ callback exception: {Detail}", e.Detail);

        return Task.CompletedTask;
    }

    private Task OnConnectionBlockedAsync(object sender, ConnectionBlockedEventArgs e)
    {
        _logger.Warning("RabbitMQ connection blocked: {Reason}", e.Reason);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disposes the RabbitMQ connection and channel.
    /// </summary>
    public void Dispose()
    {
        _channelLock.Wait();

        try
        {
            _channel?.Dispose();
            _channel = null;

            if (_lazyConnection.IsValueCreated)
            {
                _logger.Information("Closing RabbitMQ connection...");

                Connection.Dispose();
            }
        }
        finally
        {
            _channelLock.Release();
            _channelLock.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
