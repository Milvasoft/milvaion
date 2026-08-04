using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.Settings.UpdateSettings;

/// <summary>
/// Updates the application settings. Sectioned like the settings document; a null section is
/// left unchanged.
/// </summary>
public class UpdateSettingsCommand : ICommand<bool>
{
    /// <summary>
    /// Branding text (title, subtitle). Null keeps the current branding.
    /// </summary>
    public BrandingSettings Branding { get; set; }

    /// <summary>
    /// Notification rules (per-alert enable + channels). Null keeps the current notifications.
    /// </summary>
    public NotificationSettings Notifications { get; set; }
}
