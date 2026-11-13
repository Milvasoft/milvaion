using Confluent.Kafka;
using Milvasoft.Core.Abstractions;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;
using Milvasoft.Milvaion.Sdk.Worker.Options;
using Milvasoft.Milvaion.Sdk.Worker.RabbitMQ;

namespace Milvasoft.Milvaion.Sdk.Worker.Kafka;

/// <summary>
/// Sends worker → API traffic (status updates, logs, heartbeats, registrations) to a named queue, whatever the transport.
/// </summary>
internal interface IQueuePublisher : IAsyncDisposable
{
    /// <summary>
    /// Publishes <paramref name="body"/> to <paramref name="queue"/> (a <c>WorkerConstant.Queues</c> value).
    /// </summary>
    /// <param name="queue">Queue name; the Kafka transport maps it to its topic.</param>
    /// <param name="body">Message body.</param>
    /// <param name="persistent">RabbitMQ only: persistent delivery.</param>
    /// <param name="key">Kafka only: record key, so that records with the same key keep their order.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(string queue, ReadOnlyMemory<byte> body, bool persistent, string key, CancellationToken cancellationToken);
}

/// <summary>
/// Picks the publisher for the configured transport.
/// </summary>
internal static class QueuePublisherFactory
{
    /// <summary>
    /// A RabbitMQ publisher, or a Kafka one on <paramref name="kafkaClients"/> (shared clients from dependency injection;
    /// when null, the publisher builds and owns its own).
    /// </summary>
    public static IQueuePublisher Create(WorkerOptions options, IMilvaLogger logger, KafkaWorkerClients kafkaClients, params string[] queuesToDeclare)
        => options.UsesKafka
            ? new KafkaQueuePublisher(kafkaClients ?? new KafkaWorkerClients(options, logger), ownsClients: kafkaClients == null)
            : new ResilientPublisherChannel(options, logger, queuesToDeclare);
}

/// <summary>
/// Produces worker → API traffic to the Kafka topic that replaces the queue.
/// </summary>
internal sealed class KafkaQueuePublisher(KafkaWorkerClients clients, bool ownsClients) : IQueuePublisher
{
    private readonly KafkaWorkerClients _clients = clients;
    private readonly bool _ownsClients = ownsClients;

    public async Task PublishAsync(string queue, ReadOnlyMemory<byte> body, bool persistent, string key, CancellationToken cancellationToken)
    {
        var topic = KafkaTopicNames.ForQueue(_clients.Prefix, queue)
                    ?? throw new InvalidOperationException($"Queue '{queue}' has no Kafka topic.");

        await _clients.Producer.ProduceAsync(topic, new Message<string, byte[]> { Key = key, Value = body.ToArray() }, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsClients)
            _clients.Dispose();

        return ValueTask.CompletedTask;
    }
}
