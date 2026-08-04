using System.ComponentModel;
using System.Linq;
using MediatR;
using Milvaion.Application.Features.Settings.GetSettings;
using Milvaion.Application.Features.Settings.UpdateSettings;
using Milvaion.Domain.Enums;
using Milvaion.Domain.JsonModels;
using ModelContextProtocol.Server;

namespace Milvaion.Api.Mcp;

/// <summary>
/// MCP tools for the application's runtime settings: branding and per-alert notification routing.
/// Reads and writes go through the same cached settings provider as the UI, so a change here takes
/// effect at runtime across every instance.
/// </summary>
[McpServerToolType]
public class MilvaionSettingsTools(IMediator mediator, McpPermissionGuard guard)
{
    private readonly IMediator _mediator = mediator;
    private readonly McpPermissionGuard _guard = guard;

    /// <summary>
    /// Gets branding, notification rules, channel status and the available channels.
    /// </summary>
    [McpServerTool(Name = "get_settings", ReadOnly = true)]
    [Description("Gets the application's runtime settings: branding (title/subtitle), per-alert notification rules (enabled + channels), the read-only channel status from configuration, and the list of available channels. Call this first to see current values and valid channel names before changing anything.")]
    public async Task<object> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.Detail);

        var response = await _mediator.Send(new GetSettingsQuery(), cancellationToken);

        return response.Data;
    }

    /// <summary>
    /// Updates the branding text (title and subtitle).
    /// </summary>
    [McpServerTool(Name = "update_branding", ReadOnly = false)]
    [Description("Updates the application branding shown in the browser tab, sidebar and login page. Applies at runtime across all instances. The logo is a static file and is not managed here.")]
    public async Task<object> UpdateBrandingAsync(
        [Description("Application title shown in the browser tab, sidebar and login page.")] string title,
        [Description("Short subtitle/tagline shown on the login page.")] string subtitle,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.Update);

        var response = await _mediator.Send(new UpdateSettingsCommand
        {
            Branding = new BrandingSettings { Title = title, Subtitle = subtitle }
        }, cancellationToken);

        return new { success = response.Data };
    }

    /// <summary>
    /// Enables/disables a single alert type and sets its channels.
    /// </summary>
    [McpServerTool(Name = "configure_notification", ReadOnly = false)]
    [Description("Enables or disables a single alert type and sets which channels it routes to. Channel names must come from get_settings 'availableChannels' (e.g. Email, Slack, Teams, GoogleChat, InternalNotification). Applies at runtime across all instances.")]
    public async Task<object> ConfigureNotificationAsync(
        [Description("The alert type to configure.")] AlertType alertType,
        [Description("Whether this alert is sent at all.")] bool enabled,
        [Description("Channel names this alert routes to. Use names from get_settings 'availableChannels'.")] string[] channels,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.SystemAdministration.Update);

        // The update replaces the whole notifications section, so start from the current rules,
        // change only the targeted one, and send the full set back.
        var current = await _mediator.Send(new GetSettingsQuery(), cancellationToken);

        var incomingChannels = channels ?? [];

        var rules = (current.Data?.Notifications ?? [])
            .Select(r => new NotificationRule
            {
                AlertType = r.AlertType,
                Enabled = r.AlertType == alertType ? enabled : r.Enabled,
                Channels = r.AlertType == alertType ? [.. incomingChannels] : (r.Channels ?? [])
            })
            .ToList();

        if (!rules.Exists(r => r.AlertType == alertType))
            rules.Add(new NotificationRule { AlertType = alertType, Enabled = enabled, Channels = [.. incomingChannels] });

        var response = await _mediator.Send(new UpdateSettingsCommand
        {
            Notifications = new NotificationSettings { Rules = rules }
        }, cancellationToken);

        return new { success = response.Data };
    }
}
