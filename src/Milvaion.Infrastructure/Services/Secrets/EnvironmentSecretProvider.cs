using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;
using System.Text;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Reads secrets from environment variables. <c>{{secret:smtp-password}}</c> reads
/// <c>MILVAION_SECRET_SMTP_PASSWORD</c> (the prefix is configurable).
/// </summary>
public class EnvironmentSecretProvider(IOptionsMonitor<SecretsOptions> options) : ISecretProvider
{
    /// <summary>
    /// Provider name used in configuration and explicit references.
    /// </summary>
    public const string ProviderName = "env";

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var variable = ToVariableName(options.CurrentValue.Environment?.Prefix ?? "MILVAION_SECRET_", name);

        var value = System.Environment.GetEnvironmentVariable(variable);

        return Task.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>
    /// Maps a secret name to its variable: upper case, every character that is not a letter or digit
    /// becomes an underscore.
    /// </summary>
    public static string ToVariableName(string prefix, string name)
    {
        var sb = new StringBuilder(prefix, prefix.Length + name.Length);

        foreach (var c in name)
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');

        return sb.ToString();
    }
}
