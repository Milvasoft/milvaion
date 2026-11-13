using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Reads secrets from Azure Key Vault.
/// </summary>
/// <remarks>
/// <c>{{secret:azurekeyvault:smtp-password}}</c> reads the latest version of <c>smtp-password</c>;
/// <c>{{secret:azurekeyvault:smtp-password/0f2c...}}</c> reads one version. Key Vault names allow only
/// letters, digits and dashes. Credentials come from a service principal when one is configured, otherwise
/// from <c>DefaultAzureCredential</c> (managed identity, workload identity, environment, Azure CLI).
/// </remarks>
public class AzureKeyVaultSecretProvider : ISecretProvider
{
    /// <summary>
    /// Provider name used in configuration and explicit references.
    /// </summary>
    public const string ProviderName = "azurekeyvault";

    private readonly IOptionsMonitor<SecretsOptions> _options;
    private readonly Func<AzureKeyVaultSecretsOptions, SecretClient> _clientFactory;
    private readonly Lock _clientLock = new();
    private SecretClient _client;
    private string _clientKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="AzureKeyVaultSecretProvider"/> class.
    /// </summary>
    public AzureKeyVaultSecretProvider(IOptionsMonitor<SecretsOptions> options) : this(options, CreateClient)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom client factory (tests).
    /// </summary>
    public AzureKeyVaultSecretProvider(IOptionsMonitor<SecretsOptions> options, Func<AzureKeyVaultSecretsOptions, SecretClient> clientFactory)
    {
        _options = options;
        _clientFactory = clientFactory;
    }

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public async Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var settings = _options.CurrentValue.AzureKeyVault;

        if (settings == null || string.IsNullOrWhiteSpace(settings.VaultUri))
            return null;

        var slash = name.IndexOf('/');
        var secretName = slash < 0 ? name : name[..slash];
        var version = slash < 0 ? null : name[(slash + 1)..];

        try
        {
            var response = await GetClient(settings).GetSecretAsync(secretName, version, cancellationToken);

            return response?.Value?.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            throw new InvalidOperationException($"Azure Key Vault returned HTTP {ex.Status}");
        }
        catch (AuthenticationFailedException)
        {
            throw new InvalidOperationException("Azure Key Vault authentication failed");
        }
    }

    private SecretClient GetClient(AzureKeyVaultSecretsOptions settings)
    {
        var key = $"{settings.VaultUri}|{settings.TenantId}|{settings.ClientId}|{settings.ClientSecret?.GetHashCode()}";

        lock (_clientLock)
        {
            if (_client == null || _clientKey != key)
            {
                _client = _clientFactory(settings);
                _clientKey = key;
            }

            return _client;
        }
    }

    private static SecretClient CreateClient(AzureKeyVaultSecretsOptions settings)
    {
        TokenCredential credential;

        if (!string.IsNullOrWhiteSpace(settings.TenantId) && !string.IsNullOrWhiteSpace(settings.ClientId) && !string.IsNullOrWhiteSpace(settings.ClientSecret))
            credential = new ClientSecretCredential(settings.TenantId, settings.ClientId, settings.ClientSecret);
        else
            credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = string.IsNullOrWhiteSpace(settings.ClientId) ? null : settings.ClientId,
                TenantId = string.IsNullOrWhiteSpace(settings.TenantId) ? null : settings.TenantId
            });

        return new SecretClient(new Uri(settings.VaultUri), credential);
    }
}
