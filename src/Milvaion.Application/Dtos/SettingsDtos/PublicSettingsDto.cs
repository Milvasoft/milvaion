namespace Milvaion.Application.Dtos.SettingsDtos;

/// <summary>
/// The subset of settings that is safe to expose without authentication - the login page and
/// the browser tab title need it before a user signs in. Only non-secret branding text; no
/// operational config, no secrets.
/// </summary>
public class PublicSettingsDto
{
    /// <summary>Application title. Empty means the frontend uses its shipped default.</summary>
    public string Title { get; set; }

    /// <summary>Short tagline shown under the title. Optional.</summary>
    public string Subtitle { get; set; }
}
