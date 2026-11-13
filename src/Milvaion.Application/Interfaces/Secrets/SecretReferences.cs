using System.Text.RegularExpressions;

namespace Milvaion.Application.Interfaces.Secrets;

/// <summary>
/// The <c>{{secret:name}}</c> syntax.
/// </summary>
/// <remarks>
/// <para>A reference is <c>{{secret:name}}</c>, optionally with a provider: <c>{{secret:provider:name}}</c>.
/// Whitespace inside the braces is allowed (<c>{{ secret:name }}</c>). The name may contain anything except
/// whitespace, braces, quotes and backslashes, so Vault paths such as <c>apps/mail#password</c> work.</para>
/// <para>A reference can make up a whole JSON string (<c>"password": "{{secret:smtp-password}}"</c>) or part
/// of one (<c>"auth": "Bearer {{secret:api-token}}"</c>).</para>
/// </remarks>
public static partial class SecretReferences
{
    [GeneratedRegex(@"\{\{\s*secret:(?<ref>[^{}\s""\\]+)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceRegex();

    /// <summary>
    /// The compiled reference pattern. Group <c>ref</c> holds what follows <c>secret:</c>.
    /// </summary>
    public static Regex Pattern => ReferenceRegex();

    /// <summary>
    /// Whether <paramref name="text"/> contains at least one reference. Cheap enough for every dispatch.
    /// </summary>
    public static bool ContainsReference(string text) => !string.IsNullOrEmpty(text)
                                                         && text.Contains("secret:", StringComparison.Ordinal)
                                                         && ReferenceRegex().IsMatch(text);

    /// <summary>
    /// The distinct references in <paramref name="text"/>, as written after <c>secret:</c>.
    /// </summary>
    public static IReadOnlyList<string> Find(string text)
    {
        if (!ContainsReference(text))
            return [];

        return [.. ReferenceRegex().Matches(text).Select(m => m.Groups["ref"].Value).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Builds the canonical reference text for <paramref name="reference"/>.
    /// </summary>
    public static string Format(string reference) => $"{{{{secret:{reference}}}}}";
}
