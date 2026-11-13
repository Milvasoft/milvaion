using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using Milvasoft.Milvaion.Sdk.Utils;
using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Resolves <c>{{secret:name}}</c> references through the registered <see cref="ISecretProvider"/>s, with a
/// short in-memory cache.
/// </summary>
public class SecretResolver : ISecretResolver
{
    private static readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["env"] = EnvironmentSecretProvider.ProviderName,
        ["environment"] = EnvironmentSecretProvider.ProviderName,
        ["config"] = ConfigurationSecretProvider.ProviderName,
        ["configuration"] = ConfigurationSecretProvider.ProviderName,
        ["vault"] = VaultSecretProvider.ProviderName,
        ["hashicorpvault"] = VaultSecretProvider.ProviderName,
        ["azurekeyvault"] = AzureKeyVaultSecretProvider.ProviderName,
        ["azure"] = AzureKeyVaultSecretProvider.ProviderName,
        ["keyvault"] = AzureKeyVaultSecretProvider.ProviderName,
    };

    private readonly Dictionary<string, ISecretProvider> _providers;
    private readonly IOptionsMonitor<SecretsOptions> _options;
    private readonly ISecretMasker _masker;
    private readonly TimeProvider _timeProvider;
    private readonly IMilvaLogger _logger;
    private readonly ConcurrentDictionary<(string Provider, string Name), (string Value, DateTimeOffset ExpiresAt)> _cache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretResolver"/> class.
    /// </summary>
    public SecretResolver(IEnumerable<ISecretProvider> providers,
                          IOptionsMonitor<SecretsOptions> options,
                          ISecretMasker masker,
                          ILoggerFactory loggerFactory = null,
                          TimeProvider timeProvider = null)
    {
        _providers = new Dictionary<string, ISecretProvider>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
            _providers[provider.Name] = provider;

        _options = options;
        _masker = masker;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory?.CreateMilvaLogger<SecretResolver>();
    }

    /// <inheritdoc/>
    public async Task<string> ResolveAsync(string text, CancellationToken cancellationToken = default)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled || !SecretReferences.ContainsReference(text))
            return text;

        var references = SecretReferences.Find(text);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var missing = new List<string>();
        var failed = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reference in references)
        {
            var (value, failure) = await ResolveReferenceAsync(reference, options, cancellationToken);

            if (value != null)
                values[reference] = value;
            else if (failure != null)
                failed[reference] = failure;
            else
                missing.Add(reference);
        }

        if (missing.Count > 0 || failed.Count > 0)
        {
            _logger?.Warning("Secret resolution failed. Missing: {Missing}. Failed: {Failed}", string.Join(", ", missing), string.Join(", ", failed.Keys));

            throw new SecretResolutionException(missing, failed);
        }

        foreach (var (reference, value) in values)
            _masker?.Register(SecretReferences.Format(reference), value);

        return SecretReferences.Pattern.Replace(text, m => EscapeForJsonString(values[m.Groups["ref"].Value]));
    }

    /// <summary>
    /// Escapes a value for use inside a JSON string literal, so that quotes, backslashes and control
    /// characters in a secret cannot break the document.
    /// </summary>
    public static string EscapeForJsonString(string value) => JsonEncodedText.Encode(value, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();

    /// <summary>
    /// Drops every cached value. Values are fetched again on next use.
    /// </summary>
    public void ClearCache() => _cache.Clear();

    private async Task<(string Value, string Failure)> ResolveReferenceAsync(string reference, SecretsOptions options, CancellationToken cancellationToken)
    {
        var (candidates, name) = SelectProviders(reference, options);

        if (candidates.Count == 0)
            return (null, null);

        string failure = null;

        foreach (var provider in candidates)
        {
            var cacheKey = (provider.Name, name);
            var now = _timeProvider.GetUtcNow();

            if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
                return (cached.Value, null);

            string value;

            try
            {
                value = await provider.GetSecretAsync(name, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Messages of the built-in providers are written to be value-free; for anything else only the type is kept.
                var reason = ex is InvalidOperationException or TimeoutException ? ex.Message : ex.GetType().Name;

                _logger?.Warning("Secret provider {Provider} failed for {Secret}: {Reason}", provider.Name, reference, reason);

                failure ??= $"{provider.Name}: {reason}";

                continue;
            }

            if (value == null)
                continue;

            if (options.CacheTtlSeconds > 0)
                _cache[cacheKey] = (value, now.AddSeconds(options.CacheTtlSeconds));

            return (value, null);
        }

        return (null, failure);
    }

    /// <summary>
    /// <c>provider:name</c> goes to that provider only; a plain name goes through the configured order.
    /// </summary>
    private (List<ISecretProvider> Providers, string Name) SelectProviders(string reference, SecretsOptions options)
    {
        var colon = reference.IndexOf(':');

        if (colon > 0)
        {
            var prefix = reference[..colon];
            var providerName = _aliases.GetValueOrDefault(prefix, prefix);

            if (_providers.TryGetValue(providerName, out var explicitProvider))
                return ([explicitProvider], reference[(colon + 1)..]);
        }

        var ordered = new List<ISecretProvider>();

        foreach (var configured in options.Providers ?? [])
        {
            var providerName = _aliases.GetValueOrDefault(configured, configured);

            if (_providers.TryGetValue(providerName, out var provider) && !ordered.Contains(provider))
                ordered.Add(provider);
        }

        return (ordered, reference);
    }
}
