using Milvaion.Application.Dtos.ConfigurationDtos;

namespace Milvaion.Application.Dtos.SettingsDtos;

/// <summary>
/// Everything the settings page needs in one call: editable branding and notification rules,
/// plus the read-only channel status and the list of channels to pick from. One page, one GET.
/// </summary>
public class SettingsDto
{
    /// <summary>Editable branding text (title, subtitle).</summary>
    public BrandingSettings Branding { get; set; }

    /// <summary>Editable: one rule per alert type (enabled + channels).</summary>
    public List<NotificationRuleDto> Notifications { get; set; } = [];

    /// <summary>
    /// Read-only: whether each channel is configured/enabled. Comes from appsettings and cannot
    /// be changed at runtime - shown so the admin knows which channels actually deliver.
    /// </summary>
    public List<AlertChannelStatusDto> Channels { get; set; } = [];

    /// <summary>The channel names an alert can be routed to (for the picker).</summary>
    public List<string> AvailableChannels { get; set; } = [];
}

/// <summary>
/// A single editable notification rule, with the alert's name resolved for display.
/// </summary>
public class NotificationRuleDto
{
    /// <summary>The alert type.</summary>
    public AlertType AlertType { get; set; }

    /// <summary>The alert type's name, for display without a client-side enum map.</summary>
    public string AlertTypeName { get; set; }

    /// <summary>Whether this alert is sent.</summary>
    public bool Enabled { get; set; }

    /// <summary>Channels this alert routes to.</summary>
    public List<string> Channels { get; set; } = [];
}
