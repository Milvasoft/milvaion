namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// RabbitMQ configuration.
/// </summary>
public class RabbitMQConfigDto
{
    /// <summary>
    /// RabbitMQ host.
    /// </summary>
    public string Host { get; set; }

    /// <summary>
    /// RabbitMQ port.
    /// </summary>
    public int Port { get; set; }

    /// <summary>
    /// RabbitMQ virtual host.
    /// </summary>
    public string VirtualHost { get; set; }

    /// <summary>
    /// Whether the queue should be durable (survives broker restart).
    /// </summary>
    public bool Durable { get; set; }

    /// <summary>
    /// Whether the queue should auto-delete when no consumers.
    /// </summary>
    public bool AutoDelete { get; set; }

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int ConnectionTimeout { get; set; }

    /// <summary>
    /// Heartbeat interval in seconds (0 = disabled).
    /// </summary>
    public ushort Heartbeat { get; set; }

    /// <summary>
    /// Automatic connection recovery enabled.
    /// </summary>
    public bool AutomaticRecoveryEnabled { get; set; }

    /// <summary>
    /// Network recovery interval in seconds.
    /// </summary>
    public int NetworkRecoveryInterval { get; set; }

    /// <summary>
    /// Queue depth warning threshold.
    /// </summary>
    public int QueueDepthWarningThreshold { get; set; }

    /// <summary>
    /// Queue depth critical threshold.
    /// </summary>
    public int QueueDepthCriticalThreshold { get; set; }

    /// <summary>
    /// Exchange name.
    /// </summary>
    public string Exchange { get; set; }

    /// <summary>
    /// Dead letter exchange name.
    /// </summary>
    public string DeadLetterExchange { get; set; }

    /// <summary>
    /// Queue names.
    /// </summary>
    public RabbitMQQueuesDto Queues { get; set; }
}

/// <summary>
/// RabbitMQ queue names.
/// </summary>
public class RabbitMQQueuesDto
{
    /// <summary>
    /// Scheduled jobs queue name.
    /// </summary>
    public string ScheduledJobs { get; set; }

    /// <summary>
    /// Worker logs queue name.
    /// </summary>
    public string WorkerLogs { get; set; }

    /// <summary>
    /// Worker heartbeat queue name.
    /// </summary>
    public string WorkerHeartbeat { get; set; }

    /// <summary>
    /// Worker registration queue name.
    /// </summary>
    public string WorkerRegistration { get; set; }

    /// <summary>
    /// Status updates queue name.
    /// </summary>
    public string StatusUpdates { get; set; }

    /// <summary>
    /// Failed jobs queue name.
    /// </summary>
    public string FailedOccurrences { get; set; }
}
