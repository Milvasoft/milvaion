namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// RabbitMQ configuration options for job dispatcher.
/// </summary>
public class RabbitMQOptions
{
    /// <summary>
    /// Configuration section key.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:RabbitMQ";

    /// <summary>
    /// RabbitMQ host (e.g., "localhost", "rabbitmq.example.com").
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// RabbitMQ port (default: 5672).
    /// </summary>
    public int Port { get; set; } = 5672;

    /// <summary>
    /// RabbitMQ username for authentication.
    /// </summary>
    public string Username { get; set; }

    /// <summary>
    /// RabbitMQ password for authentication.
    /// </summary>
    public string Password { get; set; }

    /// <summary>
    /// Virtual host (default: "/").
    /// </summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// Whether the queue should be durable (survives broker restart).
    /// </summary>
    public bool Durable { get; set; } = true;

    /// <summary>
    /// Whether the queue should auto-delete when no consumers.
    /// </summary>
    public bool AutoDelete { get; set; } = false;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; set; } = 30;

    /// <summary>
    /// Heartbeat interval in seconds (0 = disabled).
    /// </summary>
    public ushort Heartbeat { get; set; } = 60;

    /// <summary>
    /// Automatic connection recovery enabled.
    /// </summary>
    public bool AutomaticRecoveryEnabled { get; set; } = true;

    /// <summary>
    /// Network recovery interval in seconds.
    /// </summary>
    public int NetworkRecoveryInterval { get; set; } = 10;

    /// <summary>
    /// Queue depth warning threshold.
    /// </summary>
    public int QueueDepthWarningThreshold { get; set; } = 5000;

    /// <summary>
    /// Queue depth critical threshold.
    /// </summary>
    public int QueueDepthCriticalThreshold { get; set; } = 10000;

    /// <summary>
    /// Whether the RabbitMQ Management HTTP API is enabled and accessible.
    /// When enabled, the monitoring service uses the Management API to retrieve
    /// unacknowledged message counts and discover dynamic queues.
    /// </summary>
    public bool ManagementEnabled { get; set; } = false;

    /// <summary>
    /// RabbitMQ Management HTTP API port (default: 15672).
    /// </summary>
    public int ManagementPort { get; set; } = 15672;

    /// <summary>
    /// Queue type used when declaring queues ("Classic" or "Quorum").
    /// Quorum queues require <see cref="Durable"/> to be <c>true</c> and <see cref="AutoDelete"/> to be <c>false</c>.
    /// Changing this does not migrate existing queues: an already-declared queue must be deleted before it can be
    /// re-created with a different type, since RabbitMQ rejects a redeclare whose arguments don't match.
    /// </summary>
    public RabbitMQQueueType QueueType { get; set; } = RabbitMQQueueType.Classic;

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
