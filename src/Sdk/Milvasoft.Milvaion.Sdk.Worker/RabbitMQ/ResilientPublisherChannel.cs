using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using RabbitMQ.Client;

namespace Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

/// <summary>
/// Owns the connection and channel of a publisher and rebuilds them whenever either has closed.
/// A channel closed by the broker is not restored by automatic recovery and its connection stays open,
/// so checking the connection alone leaves every later publish failing until the process restarts.
/// </summary>
/// <remarks>
/// Only the connection lifecycle is serialized. Publishes run concurrently on purpose: with confirmation tracking,
/// RabbitMQ.Client 7 assigns sequence numbers and writes frames under its own semaphore and awaits the confirm outside
/// it, whereas a lock here would make every status update of every parallel job wait for the previous broker confirm.
/// </remarks>
internal sealed class ResilientPublisherChannel(WorkerOptions options, IMilvaLogger logger, params string[] queuesToDeclare) : IAsyncDisposable
{
    private readonly WorkerOptions _options = options;
    private readonly IMilvaLogger _logger = logger;
    private readonly string[] _queuesToDeclare = queuesToDeclare;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private IConnection _connection;
    private IChannel _channel;
    private bool _disposed;

    /// <summary>
    /// Publishes to the default exchange, reconnecting first if the connection or channel has closed.
    /// </summary>
    public async Task PublishAsync(string queue, ReadOnlyMemory<byte> body, bool persistent, CancellationToken cancellationToken)
    {
        var channel = await GetOpenChannelAsync(cancellationToken);

        await channel.BasicPublishAsync(exchange: string.Empty,
                                        routingKey: queue,
                                        mandatory: false,
                                        basicProperties: new BasicProperties { Persistent = persistent },
                                        body: body,
                                        cancellationToken: cancellationToken);
    }

    private async Task<IChannel> GetOpenChannelAsync(CancellationToken cancellationToken)
    {
        if (IsUsable(_channel, _connection))
            return _channel;

        await _lifecycleLock.WaitAsync(cancellationToken);

        try
        {
            if (IsUsable(_channel, _connection))
                return _channel;

            await CloseCurrentAsync();

            var factory = new ConnectionFactory
            {
                HostName = _options.RabbitMQ.Host,
                Port = _options.RabbitMQ.Port,
                UserName = _options.RabbitMQ.Username,
                Password = _options.RabbitMQ.Password,
                VirtualHost = _options.RabbitMQ.VirtualHost,
                AutomaticRecoveryEnabled = true,
                NetworkRecoveryInterval = TimeSpan.FromSeconds(10),
                RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
            };

            try
            {
                _connection = await factory.CreateConnectionAsync(cancellationToken);

                // With PublisherConfirms on (the default), publishes await the broker's ack, per https://www.rabbitmq.com/docs/publishers#data-safety.
                var channel = await _connection.CreateChannelAsync(_options.RabbitMQ.BuildChannelOptions(), cancellationToken);

                foreach (var queue in _queuesToDeclare)
                {
                    await channel.QueueDeclareAsync(queue: queue,
                                                    durable: true,
                                                    exclusive: false,
                                                    autoDelete: false,
                                                    arguments: _options.RabbitMQ.BuildQueueArguments(),
                                                    cancellationToken: cancellationToken);
                }

                _channel = channel;

                return channel;
            }
            catch
            {
                await CloseCurrentAsync();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private static bool IsUsable(IChannel channel, IConnection connection) => channel is { IsOpen: true } && connection is { IsOpen: true };

    private async Task CloseCurrentAsync()
    {
        var channel = _channel;
        var connection = _connection;

        _channel = null;
        _connection = null;

        try
        {
            if (channel != null)
            {
                if (channel.IsOpen)
                    await channel.CloseAsync();

                channel.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "Error closing RabbitMQ publisher channel");
        }

        try
        {
            if (connection != null)
            {
                if (connection.IsOpen)
                    await connection.CloseAsync();

                connection.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(ex, "Error closing RabbitMQ publisher connection");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _lifecycleLock.WaitAsync();

        try
        {
            await CloseCurrentAsync();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        _lifecycleLock.Dispose();
    }
}
