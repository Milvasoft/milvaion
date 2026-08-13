namespace Milvaion.Application.Utils.Models.Options;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

/// <summary>
/// External identity settings. Bound from <c>MilvaionConfig:Authentication</c>. Both providers are
/// off by default, so the application keeps its local username/password behaviour until one is
/// enabled. Read at startup: changing these needs a restart.
/// </summary>
public class AuthenticationOptions
{
    /// <summary>OpenID Connect provider (e.g. Keycloak). Used for browser redirect SSO.</summary>
    public OidcOptions Oidc { get; set; } = new();

    /// <summary>Direct LDAP/Active Directory bind. Used for username/password verified by the directory.</summary>
    public LdapOptions Ldap { get; set; } = new();

    /// <summary>
    /// Delete externally provisioned users who have not signed in for this many days. Local users are never touched. 0 disables pruning.
    /// </summary>
    public int InactiveUserRetentionDays { get; set; } = 90;
}

/// <summary>
/// OpenID Connect settings. When enabled, tokens issued by <see cref="Authority"/> are accepted as
/// a second bearer scheme alongside Milvaion's own tokens; permissions are still resolved from the
/// local role model.
/// </summary>
public class OidcOptions
{
    public bool Enabled { get; set; }

    /// <summary>
    /// Realm/issuer URL as the browser addresses it, e.g. <c>https://keycloak.example.com/realms/milvaion</c>.
    /// This is the value handed to the SPA and, unless <see cref="MetadataAddress"/> overrides it, the one the
    /// API uses to discover metadata and keys.
    /// </summary>
    public string Authority { get; set; }

    /// <summary>
    /// Optional discovery document URL the API uses instead of <see cref="Authority"/>. Set this when the API
    /// reaches the provider on a different host than the browser does (for example a containerized API reaching
    /// Keycloak at <c>host.docker.internal</c> while the browser uses <c>localhost</c>). The JWKS endpoint is
    /// taken from this document, so it must be reachable from the API.
    /// </summary>
    public string MetadataAddress { get; set; }

    /// <summary>
    /// Optional comma-separated list of issuer values to accept, for when the browser and the API reach the
    /// provider on different hosts and the token issuer can be either. Empty falls back to the issuer from the
    /// discovery document.
    /// </summary>
    public string ValidIssuers { get; set; }

    /// <summary>Expected token audience (usually the client id). Empty disables audience validation.</summary>
    public string Audience { get; set; }

    /// <summary>Public client id the SPA uses for the redirect flow.</summary>
    public string ClientId { get; set; }

    /// <summary>Optional client secret for a confidential client. Not required to validate tokens.</summary>
    public string ClientSecret { get; set; }

    /// <summary>Reject non-HTTPS metadata/authority. Keep true outside local development.</summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>Claim used as the user name / display identity.</summary>
    public string NameClaim { get; set; } = "preferred_username";

    /// <summary>Stable per-user identifier claim used to link the shadow record.</summary>
    public string SubjectClaim { get; set; } = "sub";

    /// <summary>Claim carrying the user's roles/groups. Map a flattened claim in the IdP (e.g. a client role mapper).</summary>
    public string RolesClaim { get; set; } = "roles";

    /// <summary>Optional prefix filter: only roles starting with this are provisioned. Empty takes them all.</summary>
    public string RolePrefix { get; set; }
}

/// <summary>
/// Direct LDAP/Active Directory settings. When enabled, users flagged as LDAP verify their password
/// by binding to the directory instead of the local hash; Milvaion still issues its own token.
/// </summary>
public class LdapOptions
{
    public bool Enabled { get; set; }

    public string Host { get; set; }

    public int Port { get; set; } = 389;

    /// <summary>Use LDAPS (typically port 636). Credentials must never cross an unencrypted connection.</summary>
    public bool UseSsl { get; set; }

    /// <summary>Search base for user and group lookups, e.g. <c>dc=corp,dc=example,dc=com</c>.</summary>
    public string BaseDn { get; set; }

    /// <summary>Service account DN used to look up the user entry and groups. Optional if binding as the user is enough.</summary>
    public string BindDn { get; set; }

    /// <summary>Service account password for <see cref="BindDn"/>.</summary>
    public string BindPassword { get; set; }

    /// <summary>
    /// Template used to form the bind DN from the username, e.g. <c>{0}@corp.example.com</c> or
    /// <c>uid={0},ou=users,dc=corp,dc=example,dc=com</c>. <c>{0}</c> is the submitted username.
    /// </summary>
    public string UserDnFormat { get; set; }

    /// <summary>Filter to locate the user entry for group resolution, e.g. <c>(sAMAccountName={0})</c>.</summary>
    public string UserSearchFilter { get; set; } = "(sAMAccountName={0})";

    /// <summary>Attribute on the user entry listing group memberships.</summary>
    public string GroupMemberAttribute { get; set; } = "memberOf";

    /// <summary>Optional prefix filter for the groups mapped into Milvaion roles. Empty takes them all.</summary>
    public string RolePrefix { get; set; }
}

#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
