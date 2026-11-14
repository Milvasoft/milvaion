namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Kafka configuration, without credentials.
/// </summary>
public class KafkaConfigDto
{
    /// <summary>
    /// Bootstrap servers.
    /// </summary>
    public string BootstrapServers { get; set; }

    /// <summary>
    /// Security protocol.
    /// </summary>
    public string SecurityProtocol { get; set; }

    /// <summary>
    /// SASL mechanism, when SASL is used.
    /// </summary>
    public string SaslMechanism { get; set; }

    /// <summary>
    /// Prefix of every topic and consumer group.
    /// </summary>
    public string TopicPrefix { get; set; }

    /// <summary>
    /// Whether missing topics are created at startup.
    /// </summary>
    public bool AutoCreateTopics { get; set; }

    /// <summary>
    /// Partitions of auto-created topics.
    /// </summary>
    public int NumPartitions { get; set; }

    /// <summary>
    /// Replication factor of auto-created topics (-1 is the broker default).
    /// </summary>
    public short ReplicationFactor { get; set; }

    /// <summary>
    /// Consumer lag at which a topic is reported with a warning.
    /// </summary>
    public long LagWarningThreshold { get; set; }

    /// <summary>
    /// Consumer lag at which a topic is reported as critical.
    /// </summary>
    public long LagCriticalThreshold { get; set; }

    /// <summary>
    /// Consumer group of the API.
    /// </summary>
    public string ConsumerGroup { get; set; }

    /// <summary>
    /// Shared topics the API consumes.
    /// </summary>
    public List<string> Topics { get; set; }

    /// <summary>
    /// Name pattern of the per-worker job topics.
    /// </summary>
    public string JobTopicPattern { get; set; }
}
