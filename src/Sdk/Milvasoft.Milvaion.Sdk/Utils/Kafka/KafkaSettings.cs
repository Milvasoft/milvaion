namespace Milvasoft.Milvaion.Sdk.Utils.Kafka;

/// <summary>
/// Message broker a deployment uses. The API and every worker must use the same one.
/// </summary>
public enum MessageTransportType
{
    /// <summary>RabbitMQ (default).</summary>
    RabbitMQ,

    /// <summary>Apache Kafka.</summary>
    Kafka
}

/// <summary>
/// Kafka security protocol.
/// </summary>
public enum KafkaSecurityProtocol
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    Plaintext,
    Ssl,
    SaslPlaintext,
    SaslSsl
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}

/// <summary>
/// Kafka SASL mechanism.
/// </summary>
public enum KafkaSaslMechanism
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    Plain,
    ScramSha256,
    ScramSha512
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}

/// <summary>
/// Kafka connection and topic settings, shared by the API (<c>MilvaionConfig:Kafka</c>) and the .NET worker (<c>Worker:Kafka</c>).
/// </summary>
public class KafkaSettings
{
    /// <summary>
    /// Comma separated <c>host:port</c> list.
    /// </summary>
    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>
    /// Security protocol (default: <see cref="KafkaSecurityProtocol.Plaintext"/>).
    /// </summary>
    public KafkaSecurityProtocol SecurityProtocol { get; set; } = KafkaSecurityProtocol.Plaintext;

    /// <summary>
    /// SASL mechanism, used with <see cref="KafkaSecurityProtocol.SaslPlaintext"/> and <see cref="KafkaSecurityProtocol.SaslSsl"/>.
    /// </summary>
    public KafkaSaslMechanism SaslMechanism { get; set; } = KafkaSaslMechanism.Plain;

    /// <summary>SASL user name.</summary>
    public string SaslUsername { get; set; }

    /// <summary>SASL password.</summary>
    public string SaslPassword { get; set; }

    /// <summary>CA bundle used to verify the brokers' certificates.</summary>
    public string SslCaLocation { get; set; }

    /// <summary>Client certificate for mutual TLS.</summary>
    public string SslCertificateLocation { get; set; }

    /// <summary>Private key of the client certificate.</summary>
    public string SslKeyLocation { get; set; }

    /// <summary>Password of the private key.</summary>
    public string SslKeyPassword { get; set; }

    /// <summary>
    /// Prepended to every topic and consumer group name, for example <c>prod.</c>. Must be the same on the API and every worker.
    /// </summary>
    public string TopicPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Create missing topics at startup (default: <see langword="true"/>).
    /// </summary>
    public bool AutoCreateTopics { get; set; } = true;

    /// <summary>
    /// Partitions of auto-created topics (default: 12). Caps how many instances can share a topic.
    /// </summary>
    public int NumPartitions { get; set; } = 12;

    /// <summary>
    /// Replication factor of auto-created topics. -1 (default) uses the broker's <c>default.replication.factor</c>.
    /// </summary>
    public short ReplicationFactor { get; set; } = -1;

    /// <summary>
    /// Worker only: jobs of one partition that may run at the same time. 0 (default) means the worker's MaxParallelJobs.
    /// </summary>
    public int MaxInFlightPerPartition { get; set; }
}
