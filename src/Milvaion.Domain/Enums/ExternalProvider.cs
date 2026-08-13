namespace Milvaion.Domain.Enums;

/// <summary>
/// Where a user's (or role's) identity is owned. Local records are managed inside Milvaion;
/// external records are provisioned from an identity provider and their identity fields are
/// owned there, not here.
/// </summary>
public enum ExternalProvider : byte
{
    /// <summary>
    /// Managed entirely inside Milvaion: password is stored and verified locally.
    /// </summary>
    Local = 0,

    /// <summary>
    /// Federated through an OpenID Connect provider (e.g. Keycloak). The identity token is issued
    /// and validated externally; Milvaion only keeps a shadow record.
    /// </summary>
    Oidc = 1,

    /// <summary>
    /// Authenticated by a direct LDAP/Active Directory bind. Milvaion verifies the credentials
    /// against the directory and keeps a shadow record; no password is stored locally.
    /// </summary>
    Ldap = 2
}
