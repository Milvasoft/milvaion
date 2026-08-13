namespace Milvaion.Application.Interfaces;

/// <summary>
/// Verifies a username and password directly against an LDAP/Active Directory server by binding as
/// the user, and reads back the attributes and group memberships needed to provision the account.
/// Used only when the LDAP provider is enabled; the password never leaves the server unencrypted
/// (LDAPS is required for real deployments).
/// </summary>
public interface ILdapAuthenticator
{
    /// <summary>
    /// Attempts an LDAP bind with the supplied credentials. On success returns the directory
    /// attributes for the user; on failure returns a result whose <see cref="LdapAuthResult.Success"/>
    /// is false. Never throws for a wrong password.
    /// </summary>
    Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default);
}

/// <summary>
/// The outcome of an LDAP bind, plus the directory attributes read for a successful one.
/// </summary>
public sealed record LdapAuthResult
{
    /// <summary>Whether the bind succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Stable directory identifier for the user (objectGUID when available, else the bind DN).</summary>
    public string Subject { get; init; }

    /// <summary>Email address, when the directory exposes one.</summary>
    public string Email { get; init; }

    /// <summary>Given name.</summary>
    public string Name { get; init; }

    /// <summary>Family name.</summary>
    public string Surname { get; init; }

    /// <summary>Group names the user belongs to, mapped onto Milvaion roles.</summary>
    public IReadOnlyList<string> Groups { get; init; } = [];

    /// <summary>A failed bind result.</summary>
    public static LdapAuthResult Fail() => new() { Success = false };
}
