using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvaion.Application.Dtos.SettingsDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Settings.GetSettings;

/// <summary>
/// Builds the whole settings page in one response: branding and notification rules from the
/// cached runtime settings, plus channel status and the pickable channel list from appsettings.
/// </summary>
/// <param name="settingsProvider"></param>
/// <param name="alertingOptions"></param>
public class GetSettingsQueryHandler(ISettingsProvider settingsProvider, IOptions<AlertingOptions> alertingOptions) : IInterceptable, IQueryHandler<GetSettingsQuery, SettingsDto>
{
    private readonly ISettingsProvider _settingsProvider = settingsProvider;
    private readonly AlertingOptions _alertingOptions = alertingOptions.Value;

    /// <inheritdoc/>
    public async Task<Response<SettingsDto>> Handle(GetSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);

        var rules = (settings.Notifications?.Rules ?? []).Select(r => new NotificationRuleDto
        {
            AlertType = r.AlertType,
            AlertTypeName = r.AlertType.ToString(),
            Enabled = r.Enabled,
            Channels = r.Channels ?? []
        }).ToList();

        return Response<SettingsDto>.Success(new SettingsDto
        {
            Branding = settings.Branding,
            Notifications = rules,
            Channels = BuildChannelStatus(),
            AvailableChannels = [.. Enum.GetNames<AlertChannelType>()]
        });
    }

    /// <summary>
    /// Channel availability from appsettings - names match the picker values (the channel
    /// identifiers used in routes), no secrets.
    /// </summary>
    private List<AlertChannelStatusDto> BuildChannelStatus()
    {
        var channels = new List<AlertChannelStatusDto>();

        var channel = _alertingOptions.Channels;

        if (channel == null)
            return channels;

        Add(nameof(AlertChannelType.GoogleChat), channel.GoogleChat.Enabled, channel.GoogleChat.SendOnlyInProduction, channel.GoogleChat.DefaultSpace);
        Add(nameof(AlertChannelType.Slack), channel.Slack.Enabled, channel.Slack.SendOnlyInProduction, channel.Slack.DefaultChannel);
        Add(nameof(AlertChannelType.Teams), channel.Teams.Enabled, channel.Teams.SendOnlyInProduction, channel.Teams.DefaultChannel);
        Add(nameof(AlertChannelType.Email), channel.Email.Enabled, channel.Email.SendOnlyInProduction, channel.Email.DisplayName);
        Add(nameof(AlertChannelType.InternalNotification), channel.InternalNotification.Enabled, channel.InternalNotification.SendOnlyInProduction, "Dashboard");

        return channels;

        void Add(string name, bool enabled, bool? sendOnlyInProd, string target) => channels.Add(new AlertChannelStatusDto
        {
            Name = name,
            Enabled = enabled,
            SendOnlyInProduction = sendOnlyInProd ?? _alertingOptions.SendOnlyInProduction,
            DefaultTarget = target
        });
    }
}
