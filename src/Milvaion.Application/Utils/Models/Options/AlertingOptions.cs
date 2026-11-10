namespace Milvaion.Application.Utils.Models.Options;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

/// <summary>
/// Root configuration for the alerting system.
/// </summary>
public class AlertingOptions
{
    /// <summary>
    /// Configuration section key in appsettings.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Alerting";

    /// <summary>
    /// Gets or sets the base URL of the Milvaion application.
    /// Used to construct full URLs for action links in alerts.
    /// Example: "https://milvaion.example.com"
    /// </summary>
    public string MilvaionAppUrl { get; set; }

    /// <summary>
    /// Gets or sets the default channel to use when an alert type has no specific routing.
    /// </summary>
    public string DefaultChannel { get; set; } = nameof(AlertChannelType.InternalNotification);

    /// <summary>
    /// Gets or sets whether to send alerts only in production environment by default.
    /// Individual channels can override this setting.
    /// </summary>
    public bool SendOnlyInProduction { get; set; } = true;

    /// <summary>
    /// Gets or sets the channel configurations.
    /// </summary>
    public AlertChannelsOptions Channels { get; set; } = new();

    /// <summary>
    /// Gets or sets the alert type configurations with their routing.
    /// </summary>
    public Dictionary<AlertType, AlertConfig> Alerts { get; set; } = [];

    /// <summary>
    /// Builds a full action URL by combining the base URL with the given path.
    /// </summary>
    /// <param name="path">The relative path (e.g., "/jobs/123")</param>
    /// <returns>Full URL or just the path if base URL is not configured</returns>
    public string BuildActionUrl(string path)
    {
        if (string.IsNullOrWhiteSpace(MilvaionAppUrl) || string.IsNullOrWhiteSpace(path))
            return path;

        var baseUrl = MilvaionAppUrl.TrimEnd('/');
        var relativePath = path.StartsWith('/') ? path : $"/{path}";

        return $"{baseUrl}{relativePath}";
    }
}

/// <summary>
/// Container for all channel configurations.
/// </summary>
public class AlertChannelsOptions
{
    public GoogleChatChannelOptions GoogleChat { get; set; } = new();
    public SlackChannelOptions Slack { get; set; } = new();
    public TeamsChannelOptions Teams { get; set; } = new();
    public EmailChannelOptions Email { get; set; } = new();
    public InternalNotificationChannelOptions InternalNotification { get; set; } = new();
    public CrewhallChannelOptions Crewhall { get; set; } = new();
}

/// <summary>
/// Base configuration for alert channels.
/// </summary>
public abstract class ChannelConfigBase
{
    /// <summary>
    /// Gets or sets whether this channel is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets whether to send alerts only in production environment.
    /// If null, uses the global setting from <see cref="AlertingOptions.SendOnlyInProduction"/>.
    /// </summary>
    public bool? SendOnlyInProduction { get; set; }
}

/// <summary>
/// Google Chat channel configuration.
/// </summary>
public class GoogleChatChannelOptions : ChannelConfigBase
{
    /// <summary>
    /// Gets or sets the default space to send alerts to when not specified.
    /// </summary>
    public string DefaultSpace { get; set; } = "alerts";

    /// <summary>
    /// Gets or sets the configured spaces with their webhook URLs.
    /// </summary>
    public List<GoogleChatSpaceConfig> Spaces { get; set; } = [];
}

/// <summary>
/// Google Chat space configuration.
/// </summary>
public class GoogleChatSpaceConfig
{
    /// <summary>
    /// Gets or sets the space identifier/name.
    /// </summary>
    public string Space { get; set; }

    /// <summary>
    /// Gets or sets the webhook URL for this space.
    /// </summary>
    public string WebhookUrl { get; set; }
}

/// <summary>
/// Slack channel configuration.
/// </summary>
public class SlackChannelOptions : ChannelConfigBase
{
    /// <summary>
    /// Gets or sets the default channel to send alerts to when not specified.
    /// </summary>
    public string DefaultChannel { get; set; } = "alerts";

    /// <summary>
    /// Gets or sets the configured Slack channels with their webhook URLs.
    /// </summary>
    public List<SlackChannelConfig> Channels { get; set; } = [];
}

/// <summary>
/// Slack channel configuration.
/// </summary>
public class SlackChannelConfig
{
    public string Channel { get; set; }
    public string WebhookUrl { get; set; }
}

/// <summary>
/// Microsoft Teams channel configuration.
/// </summary>
public class TeamsChannelOptions : ChannelConfigBase
{
    /// <summary>
    /// Gets or sets the default channel to send alerts to when not specified.
    /// </summary>
    public string DefaultChannel { get; set; } = "alerts";

    /// <summary>
    /// Gets or sets the configured Teams channels with their webhook URLs.
    /// </summary>
    public List<TeamsChannelConfig> Channels { get; set; } = [];
}

/// <summary>
/// Teams channel configuration.
/// </summary>
public class TeamsChannelConfig
{
    public string Channel { get; set; }
    public string WebhookUrl { get; set; }
}

/// <summary>
/// Email channel configuration.
/// </summary>
public class EmailChannelOptions : ChannelConfigBase
{
    /// <summary>
    /// Gets or sets the display name for the sender.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the 'From' email address.
    /// </summary>
    public string From { get; set; }

    /// <summary>
    /// Gets or sets the sender email address for authentication.
    /// </summary>
    public string SenderEmail { get; set; }

    /// <summary>
    /// Gets or sets the sender password for authentication.
    /// </summary>
    public string SenderPassword { get; set; }

    /// <summary>
    /// Gets or sets the SMTP server host.
    /// </summary>
    public string SmtpHost { get; set; }

    /// <summary>
    /// Gets or sets the SMTP server port.
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// Gets or sets whether to use SSL for SMTP connection.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// Gets or sets the default recipient email addresses.
    /// </summary>
    public List<string> DefaultRecipients { get; set; } = [];
}

/// <summary>
/// Internal notification channel configuration.
/// </summary>
public class InternalNotificationChannelOptions : ChannelConfigBase
{
}

/// <summary>
/// Crewhall chat channel configuration.
/// </summary>
public class CrewhallChannelOptions : ChannelConfigBase
{
    /// <summary>
    /// Gets or sets the default chat to send alerts to when not specified.
    /// </summary>
    public string DefaultChat { get; set; } = "alerts";

    /// <summary>
    /// Gets or sets the configured chats with their incoming webhook URLs.
    /// </summary>
    public List<CrewhallChatConfig> Chats { get; set; } = [];
}

/// <summary>
/// Crewhall chat configuration.
/// </summary>
public class CrewhallChatConfig
{
    /// <summary>
    /// Gets or sets the chat identifier/name.
    /// </summary>
    public string Chat { get; set; }

    /// <summary>
    /// Gets or sets the incoming webhook URL of this chat. It carries the key, so it is a secret: never log or return it.
    /// </summary>
    public string WebhookUrl { get; set; }
}

/// <summary>
/// Base configuration for alert types.
/// </summary>
public class AlertConfigBase
{
    /// <summary>
    /// Gets or sets whether this alert type is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Configuration for a specific alert type including its routing.
/// </summary>
public class AlertConfig : AlertConfigBase
{
    /// <summary>
    /// Gets or sets the channels to route this alert to.
    /// </summary>
    public List<string> Routes { get; set; } = [];
}

/// <summary>
/// Defines the available alert channel types.
/// </summary>
public enum AlertChannelType
{
    GoogleChat,
    Slack,
    Teams,
    Email,
    InternalNotification,
    Crewhall
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
