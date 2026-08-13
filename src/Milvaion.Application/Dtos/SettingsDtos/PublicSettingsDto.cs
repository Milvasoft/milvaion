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

    /// <summary>
    /// Public OIDC parameters the login page needs to start the redirect flow. Non-secret: the
    /// authority and client id are meant to be seen by the browser. Disabled means no SSO button.
    /// </summary>
    public PublicOidcDto Oidc { get; set; } = new();

    /// <summary>
    /// Whether LDAP/AD login is available. Only the on/off flag is exposed: LDAP uses the normal
    /// username/password form, so the browser needs nothing else (host, base dn and the service
    /// account stay server-side).
    /// </summary>
    public PublicLdapDto Ldap { get; set; } = new();
}

/// <summary>
/// The non-secret LDAP hint for the login page: just whether directory login is on. No host, base
/// dn or service credentials are ever sent to the browser.
/// </summary>
public class PublicLdapDto
{
    /// <summary>Whether LDAP/AD login is available. The local username/password form is used for it.</summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// The non-secret OIDC parameters exposed to the login page: enough to start an authorization code
/// (PKCE) redirect, nothing more.
/// </summary>
public class PublicOidcDto
{
    /// <summary>Whether SSO is available. When false the login page shows only the local form.</summary>
    public bool Enabled { get; set; }

    /// <summary>Realm/issuer URL the browser redirects to.</summary>
    public string Authority { get; set; }

    /// <summary>Public client id the SPA authenticates as.</summary>
    public string ClientId { get; set; }
}
