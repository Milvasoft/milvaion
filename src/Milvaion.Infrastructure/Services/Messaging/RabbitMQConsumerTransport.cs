using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Infrastructure.Extensions;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Milvaion.Infrastructure.Services.Messaging;

/// <summary>
/// Consumes RabbitMQ queues on channels of the shared connection.
/// </summary>
public sealed class RabbitMQConsumerTransport(RabbitMQConnectionFactory connectionFactory, IOptions<RabbitMQOptions> options, ILoggerFactory loggerFactory) : IMessageConsumerTransport
{
    private readonly RabbitMQConnectionFactory _connectionFactory = connectionFactory;
    private readonly RabbitMQOptions _options = options.Value;
    private readonly IMilvaLogger _logger = loggerFactory.CreateMilvaLogger<RabbitMQConsumerTransport>();

    /// <inheritdoc/>
    public string Name => "RabbitMQ";

    /// <inheritdoc/>
    public async Task<IMessageConsumer> StartConsumerAsync(ConsumerSubscription subscription, Func<IMessageDelivery, Task> handler, CancellationToken cancellationToken)
    {
        var channel = await _connectionFactory.CreateChannelAsync(cancellationToken);
        var consumerHandle = new RabbitMQMessageConsumer(channel, _logger);

        try
        {
            channel.ChannelShutdownAsync += (_, args) =>
            {
                _logger.Warning("{Service} RabbitMQ channel shutdown. Reason: {Reason}", subscription.ServiceName, args.ReplyText);
                return Task.CompletedTask;
            };

            if (subscription.DeclareQueue)
            {
                await channel.QueueDeclareAsync(queue: subscription.Queue,
                                                durable: true,
                                                exclusive: false,
                                                autoDelete: false,
                                                arguments: _options.BuildQueueArguments(),
                                                cancellationToken: cancellationToken);
            }

            await channel.BasicQosAsync(0, subscription.Prefetch, false, cancellationToken);

            var consumer = new AsyncEventingBasicConsumer(channel);

            consumer.ReceivedAsync += async (_, ea) =>
            {
                try
                {
                    await handler(new RabbitMQDelivery(channel, ea, consumerHandle.ChannelLock, _logger));
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in {Service} consumer handler. DeliveryTag: {DeliveryTag}, CorrelationId: {CorrelationId}", subscription.ServiceName, ea.DeliveryTag, ea.BasicProperties?.CorrelationId);
                }
            };

            consumer.ShutdownAsync += (_, args) =>
            {
                _logger.Warning("{Service} consumer shutdown. Reason: {Reason}", subscription.ServiceName, args.ReplyText);
                return Task.CompletedTask;
            };

            var consumerTag = await channel.BasicConsumeAsync(queue: subscription.Queue,
                                                              autoAck: false,
                                                              consumer: consumer,
                                                              cancellationToken: cancellationToken);

            _logger.Information("{Service} consuming from RabbitMQ queue {Queue}. ConsumerTag: {ConsumerTag}, ChannelNumber: {ChannelNumber}, Prefetch: {Prefetch}", subscription.ServiceName, subscription.Queue, consumerTag, channel.ChannelNumber, subscription.Prefetch);

            return consumerHandle;
        }
        catch
        {
            await consumerHandle.DisposeAsync();
            throw;
        }
    }

    private sealed class RabbitMQMessageConsumer : IMessageConsumer
    {
        private readonly IChannel _channel;
        private readonly IMilvaLogger _logger;
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public RabbitMQMessageConsumer(IChannel channel, IMilvaLogger logger)
        {
            _channel = channel;
            _logger = logger;

            channel.ChannelShutdownAsync += (_, _) =>
            {
                _closed.TrySetResult();
                return Task.CompletedTask;
            };

            // The channel may have closed before the handler above was attached.
            if (channel.IsClosed)
                _closed.TrySetResult();
        }

        // Channel thread-safety: IChannel is NOT thread-safe, serialize ACK/NACK operations
        public SemaphoreSlim ChannelLock { get; } = new(1, 1);

        public Task Completion => _closed.Task;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            await _channel.SafeCloseAsync(_logger, CancellationToken.None);

            ChannelLock.Dispose();
        }
    }
}

/// <summary>
/// A RabbitMQ delivery. Ack and nack go to the channel that delivered it, since a delivery tag is only valid there.
/// </summary>
public sealed class RabbitMQDelivery(IChannel channel, BasicDeliverEventArgs ea, SemaphoreSlim channelLock, IMilvaLogger logger) : IMessageDelivery
{
    private readonly IChannel _channel = channel;
    private readonly SemaphoreSlim _channelLock = channelLock;
    private readonly IMilvaLogger _logger = logger;

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> Body { get; } = ea.Body;

    /// <inheritdoc/>
    public string MessageId { get; } = ea.BasicProperties?.MessageId;

    /// <inheritdoc/>
    public string CorrelationId { get; } = ea.BasicProperties?.CorrelationId;

    /// <inheritdoc/>
    public IDictionary<string, object> Headers { get; } = ea.BasicProperties?.Headers;

    /// <inheritdoc/>
    public ulong DeliveryTag { get; } = ea.DeliveryTag;

    /// <inheritdoc/>
    public Task AckAsync(CancellationToken cancellationToken) => _channel.SafeAckAsync(DeliveryTag, _channelLock, _logger, cancellationToken);

    /// <inheritdoc/>
    public Task NackAsync(bool requeue, CancellationToken cancellationToken) => _channel.SafeNackAsync(DeliveryTag, _channelLock, _logger, cancellationToken, requeue: requeue);
}
