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
}
