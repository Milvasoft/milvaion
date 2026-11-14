using Milvasoft.Milvaion.Sdk.Utils.Kafka;

namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Selects the message broker (<c>MilvaionConfig:Transport</c>).
/// </summary>
public class TransportOptions
{
    /// <summary>
    /// Configuration section key.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Transport";

    /// <summary>
    /// <see cref="MessageTransportType.RabbitMQ"/> (default) or <see cref="MessageTransportType.Kafka"/>.
    /// </summary>
    public MessageTransportType Type { get; set; } = MessageTransportType.RabbitMQ;
}

/// <summary>
/// Kafka settings of the API (<c>MilvaionConfig:Kafka</c>). Used when <see cref="TransportOptions.Type"/> is Kafka.
/// </summary>
public class KafkaOptions : KafkaSettings
{
    /// <summary>
    /// Configuration section key.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Kafka";

    /// <summary>
    /// Consumer lag at which a topic is reported with a warning (default: 5000).
    /// </summary>
    public long LagWarningThreshold { get; set; } = 5000;

    /// <summary>
    /// Consumer lag at which a topic is reported as critical (default: 10000).
    /// </summary>
    public long LagCriticalThreshold { get; set; } = 10000;
}
