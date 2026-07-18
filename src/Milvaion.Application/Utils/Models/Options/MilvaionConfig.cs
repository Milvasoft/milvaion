namespace Milvaion.Application.Utils.Models.Options;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public class MilvaionConfig
{
    /// <summary>
    /// Optional base path for the application (e.g. "/milvaion").
    /// When set, the UI is served at &lt;BasePath&gt; and the API at &lt;BasePath&gt;/api.
    /// Leave empty or null to keep the default root behaviour.
    /// </summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>
    /// Api key signing and versioning settings.
    /// </summary>
    public ApiKeyOptions ApiKey { get; set; }

    public RedisOptions Redis { get; set; }
    public RabbitMQOptions RabbitMQ { get; set; }
    public JobDispatcherOptions JobDispatcher { get; set; }
    public ZombieOccurrenceDetectorOptions ZombieOccurrenceDetector { get; set; }
    public JobAutoDisableOptions JobAutoDisable { get; set; }
    public AlertingOptions Alerting { get; set; }
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
