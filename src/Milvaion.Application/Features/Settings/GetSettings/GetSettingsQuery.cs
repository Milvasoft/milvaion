using Milvaion.Application.Dtos.SettingsDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Settings.GetSettings;

/// <summary>
/// Query for everything the settings page shows (admin): branding, notification rules, channel
/// status and the available channels - one call for the whole page.
/// </summary>
public record GetSettingsQuery : IQuery<SettingsDto>
{
}
