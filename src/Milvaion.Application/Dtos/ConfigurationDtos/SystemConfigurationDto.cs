using Milvasoft.Attributes.Annotations;

namespace Milvaion.Application.Dtos.ConfigurationDtos;

/// <summary>
/// Data transfer object for system configuration.
/// </summary>
[Translate]
public class SystemConfigurationDto
{
    /// <summary>
    /// Application version.
    /// </summary>
    public string Version { get; set; }

    /// <summary>
    /// Environment name (Development, Staging, Production).
    /// </summary>
    public string Environment { get; set; }

    /// <summary>
    /// Server hostname.
    /// </summary>
    public string HostName { get; set; }

    /// <summary>
    /// Application startup time.
    /// </summary>
    public DateTime StartupTime { get; set; }

    /// <summary>
    /// Application uptime.
    /// </summary>
    public TimeSpan Uptime { get; set; }

    /// <summary>
    /// System resources (CPU, Memory, Disk).
    /// </summary>
    public SystemResourcesDto SystemResources { get; set; }

    /// <summary>
    /// Job dispatcher configuration.
    /// </summary>
    public JobDispatcherConfigDto JobDispatcher { get; set; }

    /// <summary>
    /// Database configuration.
    /// </summary>
    public DatabaseConfigDto Database { get; set; }

    /// <summary>
    /// Redis configuration.
    /// </summary>
    public RedisConfigDto Redis { get; set; }

    /// <summary>
    /// RabbitMQ configuration.
    /// </summary>
    public RabbitMQConfigDto RabbitMQ { get; set; }

    /// <summary>
    /// Job auto-disable configuration.
    /// </summary>
    public JobAutoDisableOptions JobAutoDisable { get; set; }
}