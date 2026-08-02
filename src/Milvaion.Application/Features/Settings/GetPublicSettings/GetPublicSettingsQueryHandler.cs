using Milvaion.Application.Dtos.SettingsDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Settings.GetPublicSettings;

/// <summary>
/// Handles the public settings query. Returns only non-secret branding text.
/// </summary>
/// <param name="settingsProvider"></param>
public class GetPublicSettingsQueryHandler(ISettingsProvider settingsProvider) : IInterceptable, IQueryHandler<GetPublicSettingsQuery, PublicSettingsDto>
{
    private readonly ISettingsProvider _settingsProvider = settingsProvider;

    /// <inheritdoc/>
    public async Task<Response<PublicSettingsDto>> Handle(GetPublicSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        var branding = settings.Branding ?? new BrandingSettings();

        return Response<PublicSettingsDto>.Success(new PublicSettingsDto
        {
            Title = branding.Title,
            Subtitle = branding.Subtitle
        });
    }
}
