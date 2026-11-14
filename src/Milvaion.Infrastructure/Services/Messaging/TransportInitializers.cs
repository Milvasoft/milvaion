using Milvaion.Application.Interfaces.Messaging;
using Milvaion.Infrastructure.Services.Kafka;
using Milvaion.Infrastructure.Services.RabbitMQ;
using Milvasoft.Milvaion.Sdk.Utils.Kafka;

namespace Milvaion.Infrastructure.Services.Messaging;

/// <summary>
/// Declares the RabbitMQ exchanges and queues the API needs.
/// </summary>
public sealed class RabbitMQTransportInitializer(RabbitMQConnectionFactory connectionFactory) : IMessageTransportInitializer
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default) => connectionFactory.InitializeAsync(cancellationToken);
}

/// <summary>
/// Creates the shared Kafka topics the API consumes. Worker job topics are created by the workers.
/// </summary>
public sealed class KafkaTransportInitializer(KafkaClientFactory clientFactory) : IMessageTransportInitializer
{
    /// <inheritdoc/>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => clientFactory.EnsureTopicsAsync(KafkaTopicNames.SharedTopics(clientFactory.Prefix), cancellationToken);
}
