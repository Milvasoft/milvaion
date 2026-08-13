using Milvaion.Application.Dtos.SettingsDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Settings.GetPublicSettings;

/// <summary>
/// Handles the public settings query. Returns only non-secret branding text.
/// </summary>
/// <param name="settingsProvider"></param>
/// <param name="milvaionConfig"></param>
public class GetPublicSettingsQueryHandler(ISettingsProvider settingsProvider, MilvaionConfig milvaionConfig) : IInterceptable, IQueryHandler<GetPublicSettingsQuery, PublicSettingsDto>
{
    private readonly ISettingsProvider _settingsProvider = settingsProvider;
    private readonly MilvaionConfig _milvaionConfig = milvaionConfig;

    /// <inheritdoc/>
    public async Task<Response<PublicSettingsDto>> Handle(GetPublicSettingsQuery request, CancellationToken cancellationToken)
    {
        var settings = await _settingsProvider.GetAsync(cancellationToken);
        var branding = settings.Branding ?? new BrandingSettings();
        var oidc = _milvaionConfig?.Authentication?.Oidc ?? new OidcOptions();
        var ldap = _milvaionConfig?.Authentication?.Ldap ?? new LdapOptions();

        return Response<PublicSettingsDto>.Success(new PublicSettingsDto
        {
            Title = branding.Title,
            Subtitle = branding.Subtitle,
            Oidc = new PublicOidcDto
            {
                Enabled = oidc.Enabled,
                Authority = oidc.Authority,
                ClientId = oidc.ClientId
            },
            Ldap = new PublicLdapDto
            {
                Enabled = ldap.Enabled
            }
        });
    }
}
