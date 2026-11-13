using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Reads secrets from <c>MilvaionConfig:Secrets:Values</c>. Any configuration source works: environment
/// variables (<c>MilvaionConfig__Secrets__Values__smtp-password</c>), a mounted JSON file, user secrets.
/// </summary>
public class ConfigurationSecretProvider(IOptionsMonitor<SecretsOptions> options) : ISecretProvider
{
    /// <summary>
    /// Provider name used in configuration and explicit references.
    /// </summary>
    public const string ProviderName = "config";

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var values = options.CurrentValue.Values;

        if (values == null || values.Count == 0)
            return Task.FromResult<string>(null);

        // The binder may have replaced the dictionary with a case-sensitive one, so fall back to a scan.
        if (values.TryGetValue(name, out var value) && value != null)
            return Task.FromResult(value);

        foreach (var (key, candidate) in values)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(candidate);
        }

        return Task.FromResult<string>(null);
    }
}
