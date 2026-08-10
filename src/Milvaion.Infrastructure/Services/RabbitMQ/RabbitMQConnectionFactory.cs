using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Infrastructure.Extensions;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Milvaion.Infrastructure.Services.RabbitMQ;

/// <summary>
/// Manages RabbitMQ connection lifecycle.
/// Provides shared connection with per-caller channel creation.
/// </summary>
/// <remarks>
/// RabbitMQ Best Practice:
/// - 1 Connection per application (TCP socket, heavy)
/// - 1 Channel per operation (lightweight, NOT thread-safe)
/// - Caller is responsible for channel disposal
///
/// Usage:
/// 1. Call InitializeAsync() once at application startup
/// 2. Use CreateChannelAsync() for each operation
/// 3. Dispose channel after use (await using)
/// </remarks>
/// <remarks>
/// Initializes a new instance of the <see cref="RabbitMQConnectionFactory"/> class.
/// </remarks>
public class RabbitMQConnectionFactory(IOptions<RabbitMQOptions> options, ILoggerFactory loggerFactory) : IAsyncDisposable, IDisposable
{
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<RabbitMQConnectionFactory>();
    private readonly RabbitMQOptions _options = options.Value;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private IConnection _connection;
    private bool _disposed;
    private volatile bool _initialized;

    /// <summary>
    /// Gets the active RabbitMQ connection.
    /// Throws if InitializeAsync() has not been called.
    /// </summary>
    public IConnection Connection => _connection ?? throw new InvalidOperationException("RabbitMQ connection not initialized. Call InitializeAsync() first.");

    /// <summary>
    /// Initializes connection, queues and exchanges.
    /// Must be called once at application startup before using CreateChannelAsync().
    /// Thread-safe and idempotent - safe to call multiple times from multiple threads.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Fast path - already initialized
        if (_initialized)
            return;

        await _initLock.WaitAsync(cancellationToken);

        try
        {
            // Double-check after acquiring lock
            if (_initialized)
                return;

            await InitializeCoreAsync(cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Core initialization logic - creates connection and declares queues/exchanges.
    /// </summary>
    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        // Create connection
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

        _connection = await factory.CreateConnectionAsync(cancellationToken);

        _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
        _connection.CallbackExceptionAsync += OnCallbackExceptionAsync;
        _connection.ConnectionBlockedAsync += OnConnectionBlockedAsync;
        _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;

        _logger.Information("RabbitMQ connection established: {Host}:{Port}, VirtualHost: {VirtualHost}",
            _options.Host, _options.Port, _options.VirtualHost);

        // Declare all queues and exchanges
        await DeclareTopologyAsync(cancellationToken);

        _logger.Information("RabbitMQ initialized. Jobs queue: {Queue}, DLQ: {DLQ}, DLX: {DLX}, Exchange: {Exchange}",
                            WorkerConstant.Queues.Jobs,
                            WorkerConstant.Queues.FailedOccurrences,
                            WorkerConstant.DeadLetterExchangeName,
                            WorkerConstant.ExchangeName);
    }

    /// <summary>
    /// Declares all queues, exchanges and bindings.
    /// Called on initial connection and after recovery.
    /// </summary>
    private async Task DeclareTopologyAsync(CancellationToken cancellationToken = default)
    {
        await using var channel = await _connection.CreateChannelAsync(cancellationToken: cancellationToken);

        // 1. Topic Exchange for job routing
        await channel.ExchangeDeclareAsync(exchange: WorkerConstant.ExchangeName,
                                           type: "topic",
                                           durable: true,
                                           autoDelete: false,
                                           arguments: null,
                                           cancellationToken: cancellationToken);

        // 2. Dead Letter Exchange (DLX)
        await channel.ExchangeDeclareAsync(exchange: WorkerConstant.DeadLetterExchangeName,
                                           type: "direct",
                                           durable: true,
                                           autoDelete: false,
                                           arguments: null,
                                           cancellationToken: cancellationToken);

        // 3. Dead Letter Queue (DLQ)
        await channel.QueueDeclareAsync(queue: WorkerConstant.Queues.FailedOccurrences,
                                        durable: true,
                                        exclusive: false,
                                        autoDelete: false,
                                        arguments: _options.BuildQueueArguments(),
                                        cancellationToken: cancellationToken);

        // 4. Bind DLQ to DLX
        await channel.QueueBindAsync(queue: WorkerConstant.Queues.FailedOccurrences,
                                     exchange: WorkerConstant.DeadLetterExchangeName,
                                     routingKey: WorkerConstant.DeadLetterRoutingKey,
                                     arguments: null,
                                     cancellationToken: cancellationToken);

        // 5. Main Jobs Queue with DLX settings
        var mainQueueArgs = new Dictionary<string, object>
        {
            { "x-dead-letter-exchange", WorkerConstant.DeadLetterExchangeName },
            { "x-dead-letter-routing-key", WorkerConstant.DeadLetterRoutingKey }
        };

        await channel.QueueDeclareAsync(queue: WorkerConstant.Queues.Jobs,
                                        durable: _options.Durable,
                                        exclusive: false,
                                        autoDelete: _options.AutoDelete,
                                        arguments: _options.BuildQueueArguments(mainQueueArgs),
                                        cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Creates a new channel. Caller is responsible for disposal.
    /// </summary>
    /// <param name="enablePublisherConfirms">
    /// Whether this channel is going to publish. Confirms are only meaningful on a publishing
    /// channel, so consume-only callers leave this false and never pay for them.
    ///
    /// Asking for them is not the same as getting them: <see cref="RabbitMQOptions.PublisherConfirms"/>
    /// can switch them off for the whole application, and this parameter is combined with it. When
    /// they are on, BasicPublishAsync only completes once the broker has acked the message, throwing
    /// on nack or timeout, so callers must not consider a message safely published until the publish
    /// call returns without an exception. See https://www.rabbitmq.com/docs/publishers#data-safety
    /// </param>
    /// <param name="cancellationToken"></param>
    public async Task<IChannel> CreateChannelAsync(bool enablePublisherConfirms = false, CancellationToken cancellationToken = default)
    {
        if (!_initialized)
            throw new InvalidOperationException("RabbitMQ not initialized. Call InitializeAsync() first.");

        var useConfirms = enablePublisherConfirms && _options.PublisherConfirms;

        var channelOptions = useConfirms
            ? new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true)
            : null;

        var channel = await _connection.CreateChannelAsync(channelOptions, cancellationToken);

        _logger.Debug("Channel created: {ChannelNumber} (PublisherConfirms: {PublisherConfirms})", channel.ChannelNumber, useConfirms);

        return channel;
    }

    /// <summary>
    /// Creates a new channel with the given cancellation token, publisher confirms disabled. Caller is responsible for disposal.
    /// </summary>
    public Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken) => CreateChannelAsync(enablePublisherConfirms: false, cancellationToken);

    /// <summary>
    /// Checks if RabbitMQ connection is healthy.
    /// </summary>
    public bool IsHealthy() => _initialized && _connection != null && _connection.IsOpen;

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs e)
    {
        _logger.Warning("RabbitMQ connection shutdown: {ReplyCode} - {ReplyText}", e.ReplyCode, e.ReplyText);

        // Reset initialized flag so topology is re-declared after recovery
        _initialized = false;

        return Task.CompletedTask;
    }

    private async Task OnRecoverySucceededAsync(object sender, AsyncEventArgs e)
    {
        _logger.Information("RabbitMQ connection recovered. Re-declaring topology...");

        try
        {
            // Re-declare topology after recovery
            await DeclareTopologyAsync();

            _initialized = true;

            _logger.Information("RabbitMQ topology re-declared after recovery");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to re-declare topology after recovery");
        }
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
    /// Disposes the RabbitMQ connection asynchronously.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_connection != null)
        {
            _logger.Information("Closing RabbitMQ connection...");

            await _connection.SafeCloseAsync(_logger, CancellationToken.None);
        }

        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the RabbitMQ connection synchronously.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_connection != null)
        {
            _logger.Information("Closing RabbitMQ connection...");

            _connection.Dispose();
        }

        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }
}