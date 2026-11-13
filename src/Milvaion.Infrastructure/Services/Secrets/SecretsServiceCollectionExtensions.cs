using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Milvaion.Application.Interfaces.Secrets;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Registers secret references support.
/// </summary>
public static class SecretsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the secret resolver, the masker, the template store and the built-in providers
    /// (environment variables, configuration, HashiCorp Vault KV v2, Azure Key Vault).
    /// </summary>
    /// <remarks>
    /// To add a provider of your own, register another <see cref="ISecretProvider"/> and list its name in
    /// <c>MilvaionConfig:Secrets:Providers</c> (or name it in references).
    /// </remarks>
    public static IServiceCollection AddSecretReferences(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SecretsOptions>().Bind(configuration.GetSection(SecretsOptions.SectionKey));

        services.AddHttpClient(VaultSecretProvider.HttpClientName);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecretProvider, EnvironmentSecretProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecretProvider, ConfigurationSecretProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecretProvider, VaultSecretProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISecretProvider, AzureKeyVaultSecretProvider>());

        services.TryAddSingleton<ISecretMasker, SecretMasker>();
        services.TryAddSingleton<ISecretResolver, SecretResolver>();
        services.TryAddSingleton<ISecretTemplateStore, RedisSecretTemplateStore>();

        return services;
    }
}
