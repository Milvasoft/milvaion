using Milvaion.Application.Dtos.SettingsDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Settings.GetPublicSettings;

/// <summary>
/// Query for the non-secret settings subset the login page and browser tab need before auth.
/// </summary>
public record GetPublicSettingsQuery : IQuery<PublicSettingsDto>
{
}
