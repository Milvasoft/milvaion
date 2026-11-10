using Milvaion.Application.Interfaces;
using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Verifies credentials against LDAP/Active Directory by binding as the user, then reads the user's
/// attributes and group memberships for provisioning. A wrong password fails the bind and returns a
/// failed result rather than throwing.
/// </summary>
public class LdapAuthenticator(MilvaionConfig config) : ILdapAuthenticator
{
    private readonly LdapOptions _options = config?.Authentication?.Ldap ?? new LdapOptions();

    /// <inheritdoc/>
    public Task<LdapAuthResult> AuthenticateAsync(string username, string password, CancellationToken cancellationToken = default)
        => Task.Run(() => Authenticate(username, password), cancellationToken);

    private LdapAuthResult Authenticate(string username, string password)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return LdapAuthResult.Fail();

        var userBindDn = string.IsNullOrWhiteSpace(_options.UserDnFormat)
            ? username
            : string.Format(_options.UserDnFormat, EscapeDnValue(username));

        try
        {
            using var connection = CreateConnection();
            connection.Bind(new NetworkCredential(userBindDn, password));

            // The bind succeeded, so the credentials are valid. Read attributes and groups if we can.
            return ReadDirectory(username, userBindDn, password);
        }
        catch (LdapException)
        {
            return LdapAuthResult.Fail();
        }
        catch (DirectoryOperationException)
        {
            return LdapAuthResult.Fail();
        }
    }

    private LdapConnection CreateConnection()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_options.Host, _options.Port))
        {
            AuthType = AuthType.Basic
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.SecureSocketLayer = _options.UseSsl;

        return connection;
    }

    private LdapAuthResult ReadDirectory(string username, string userBindDn, string userPassword)
    {
        // Without a search base there is nowhere to read groups from, but the login itself is valid.
        if (string.IsNullOrWhiteSpace(_options.BaseDn))
            return new LdapAuthResult { Success = true, Subject = userBindDn, Groups = [] };

        try
        {
            using var searchConnection = CreateConnection();

            // Prefer a service account for the lookup; otherwise reuse the user's own bind.
            if (!string.IsNullOrWhiteSpace(_options.BindDn))
                searchConnection.Bind(new NetworkCredential(_options.BindDn, _options.BindPassword));
            else
                searchConnection.Bind(new NetworkCredential(userBindDn, userPassword));

            var filter = string.Format(_options.UserSearchFilter ?? "(sAMAccountName={0})", EscapeFilterValue(username));

            var request = new SearchRequest(_options.BaseDn, filter, SearchScope.Subtree,
                                            "mail", "givenName", "sn", "objectGUID", _options.GroupMemberAttribute);

            var response = (SearchResponse)searchConnection.SendRequest(request);

            if (response.Entries.Count == 0)
                return new LdapAuthResult { Success = true, Subject = userBindDn, Groups = [] };

            // With an OR-shaped search filter a login name can match several people. Reading the first
            // one would hand this user somebody else's groups.
            if (response.Entries.Count > 1)
                return new LdapAuthResult { Success = true, Subject = userBindDn, Groups = [] };

            var entry = response.Entries[0];

            return new LdapAuthResult
            {
                Success = true,
                Subject = ExtractSubject(entry) ?? userBindDn,
                Email = GetAttribute(entry, "mail"),
                Name = GetAttribute(entry, "givenName"),
                Surname = GetAttribute(entry, "sn"),
                Groups = ExtractGroups(entry)
            };
        }
        catch (LdapException)
        {
            // The bind worked but the lookup did not: still a valid login, just without roles this time.
            return new LdapAuthResult { Success = true, Subject = userBindDn, Groups = [] };
        }
    }

    /// <summary>
    /// Escapes a value for use inside an LDAP search filter (RFC 4515).
    /// </summary>
    internal static string EscapeFilterValue(string value)
    {
        var escaped = new StringBuilder(value.Length);

        foreach (var c in value)
        {
            escaped.Append(c switch
            {
                '\\' => @"\5c",
                '*' => @"\2a",
                '(' => @"\28",
                ')' => @"\29",
                '\0' => @"\00",
                _ => c.ToString()
            });
        }

        return escaped.ToString();
    }

    /// <summary>
    /// Escapes a value for use as an attribute value inside a distinguished name (RFC 4514).
    /// </summary>
    internal static string EscapeDnValue(string value)
    {
        var escaped = new StringBuilder(value.Length + 4);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            var mustEscape = c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '='
                             || (c is '#' or ' ' && i == 0)
                             || (c == ' ' && i == value.Length - 1);

            if (c == '\0')
                escaped.Append(@"\00");
            else if (mustEscape)
                escaped.Append('\\').Append(c);
            else
                escaped.Append(c);
        }

        return escaped.ToString();
    }

    private List<string> ExtractGroups(SearchResultEntry entry)
    {
        var groups = new List<string>();

        var attribute = entry.Attributes[_options.GroupMemberAttribute];

        if (attribute is null)
            return groups;

        foreach (var value in attribute.GetValues(typeof(string)).Cast<string>())
        {
            var name = ExtractCommonName(value);

            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!string.IsNullOrEmpty(_options.RolePrefix) && !name.StartsWith(_options.RolePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            groups.Add(name);
        }

        return groups;
    }

    /// <summary>"CN=Admins,OU=Groups,DC=corp,DC=com" becomes "Admins".</summary>
    private static string ExtractCommonName(string distinguishedName)
    {
        if (string.IsNullOrWhiteSpace(distinguishedName))
            return null;

        var first = distinguishedName.Split(',')[0];
        var separator = first.IndexOf('=');

        return separator >= 0 ? first[(separator + 1)..].Trim() : first.Trim();
    }

    private static string GetAttribute(SearchResultEntry entry, string name)
    {
        var attribute = entry.Attributes[name];

        return attribute is null || attribute.Count == 0 ? null : attribute[0]?.ToString();
    }

    private static string ExtractSubject(SearchResultEntry entry)
    {
        var attribute = entry.Attributes["objectGUID"];

        if (attribute is null || attribute.Count == 0)
            return null;

        return attribute[0] is byte[] bytes ? new Guid(bytes).ToString() : attribute[0]?.ToString();
    }
}
