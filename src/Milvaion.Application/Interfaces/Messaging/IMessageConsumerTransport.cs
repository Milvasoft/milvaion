namespace Milvaion.Application.Interfaces.Messaging;

/// <summary>
/// One message received from the broker. It must be settled exactly once with <see cref="AckAsync"/> or <see cref="NackAsync"/>.
/// </summary>
public interface IMessageDelivery
{
    /// <summary>
    /// Message body. On RabbitMQ it is only valid while the handler runs; copy it to keep it.
    /// </summary>
    ReadOnlyMemory<byte> Body { get; }

    /// <summary>
    /// Message id, if the publisher set one.
    /// </summary>
    string MessageId { get; }

    /// <summary>
    /// Correlation id (the occurrence id on job traffic), if the publisher set one.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    /// Headers. Values may be numbers, strings or raw bytes; read them with <c>MessageHeaderValues</c>.
    /// </summary>
    IDictionary<string, object> Headers { get; }

    /// <summary>
    /// Identifies the delivery in logs: the RabbitMQ delivery tag, or the Kafka offset.
    /// </summary>
    ulong DeliveryTag { get; }

    /// <summary>
    /// Confirms the message was handled. On Kafka its offset becomes committable.
    /// </summary>
    Task AckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rejects the message. With <paramref name="requeue"/> it is delivered again; without it, it is dropped
    /// (or dead-lettered where the queue is configured to).
    /// </summary>
    Task NackAsync(bool requeue, CancellationToken cancellationToken);
}

/// <summary>
/// What a consumer reads.
/// </summary>
/// <param name="Queue">RabbitMQ queue name (a <c>WorkerConstant.Queues</c> value); the Kafka transport maps it to its topic.</param>
/// <param name="Prefetch">Unsettled messages the consumer may hold at once.</param>
/// <param name="ServiceName">Owning service, for logs.</param>
/// <param name="DeclareQueue">RabbitMQ only: declare the queue before consuming. Off for queues the API declares at startup with special arguments.</param>
public sealed record ConsumerSubscription(string Queue, ushort Prefetch, string ServiceName, bool DeclareQueue = true);

/// <summary>
/// A running consumer. Disposing it stops it and releases the broker resources.
/// </summary>
public interface IMessageConsumer : IAsyncDisposable
{
    /// <summary>
    /// Completes when the consumer has died (channel closed, fatal broker error), so the owner can reconnect.
    /// It does not complete on cancellation: the consumer stays open so that pending messages can still be settled.
    /// </summary>
    Task Completion { get; }
}

/// <summary>
/// Starts consumers on the configured broker.
/// </summary>
public interface IMessageConsumerTransport
{
    /// <summary>
    /// Transport name for logs and alerts ("RabbitMQ" or "Kafka").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Connects and starts delivering messages of <paramref name="subscription"/> to <paramref name="handler"/>, one at a time.
    /// Throws when the broker cannot be reached.
    /// </summary>
    Task<IMessageConsumer> StartConsumerAsync(ConsumerSubscription subscription, Func<IMessageDelivery, Task> handler, CancellationToken cancellationToken);
}

/// <summary>
/// Prepares the broker at startup (RabbitMQ exchanges and queues, Kafka topics).
/// </summary>
public interface IMessageTransportInitializer
{
    /// <summary>
    /// Declares or creates what the API needs on the broker.
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
