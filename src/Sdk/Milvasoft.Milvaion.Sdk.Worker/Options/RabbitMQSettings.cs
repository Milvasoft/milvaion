using RabbitMQ.Client;

namespace Milvasoft.Milvaion.Sdk.Worker.Options;

public class RabbitMQSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5672;
    public string Username { get; set; } = "guest";
    public string Password { get; set; } = "guest";
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Routing key patterns this consumer subscribes to (e.g., ["test.*", "email.*"]).
    /// </summary>
    public string RoutingKeyPattern { get; set; }

    /// <summary>
    /// Queue type used when declaring queues ("Classic" or "Quorum"). Must match the value configured on the
    /// Milvaion API side (<c>MilvaionConfig:RabbitMQ:QueueType</c>), since RabbitMQ rejects a queue redeclare
    /// whose arguments don't match what's already stored for that queue.
    /// </summary>
    public RabbitMQQueueType QueueType { get; set; } = RabbitMQQueueType.Classic;

    /// <summary>
    /// Whether publishing channels wait for the broker to acknowledge each message (default: <see langword="true"/>).
    ///
    /// With confirms on, a publish only completes once RabbitMQ has accepted the message and throws on nack or
    /// timeout; with them off it completes as soon as the bytes leave the socket, so a broker that drops the
    /// message reports nothing and the status update, log batch or job never arrives. Turning this off trades that
    /// guarantee for one less round trip per publish.
    ///
    /// Independent of the API's <c>MilvaionConfig:RabbitMQ:PublisherConfirms</c>: unlike <see cref="QueueType"/>,
    /// this is a per-channel client concern and the two sides do not have to agree.
    /// </summary>
    public bool PublisherConfirms { get; set; } = true;

    /// <summary>
    /// Builds the queue declaration arguments for <see cref="QueueType"/>, merging in any extra arguments (e.g. dead-letter settings).
    /// Returns <see langword="null"/> for <see cref="RabbitMQQueueType.Classic"/> with no extra arguments, matching RabbitMQ's default (no arguments = classic queue).
    /// </summary>
    public Dictionary<string, object> BuildQueueArguments(Dictionary<string, object> extraArguments = null)
    {
        var arguments = extraArguments != null ? new Dictionary<string, object>(extraArguments) : null;

        if (QueueType == RabbitMQQueueType.Quorum)
        {
            arguments ??= [];
            arguments["x-queue-type"] = "quorum";
        }

        return arguments;
    }

    /// <summary>
    /// Builds the channel options for a publishing channel, honouring <see cref="PublisherConfirms"/>.
    /// </summary>
    /// <param name="consumerDispatchConcurrency">
    /// Set on channels that also consume. An options object does not inherit the value from the connection
    /// factory, so a consuming channel that omits it silently drops to dispatching one delivery at a time.
    /// </param>
    public CreateChannelOptions BuildChannelOptions(ushort? consumerDispatchConcurrency = null)
        => new(publisherConfirmationsEnabled: PublisherConfirms,
               publisherConfirmationTrackingEnabled: PublisherConfirms,
               outstandingPublisherConfirmationsRateLimiter: null,
               consumerDispatchConcurrency: consumerDispatchConcurrency);
}

/// <summary>
/// RabbitMQ queue types supported for queue declaration.
/// </summary>
public enum RabbitMQQueueType
{
    /// <summary>
    /// Default RabbitMQ queue type. Single-node replicated only via mirroring policies (deprecated in RabbitMQ).
    /// </summary>
    Classic,

    /// <summary>
    /// Raft-based replicated queue type recommended for data safety in clustered deployments. Requires Durable=true, AutoDelete=false.
    /// </summary>
    Quorum
}