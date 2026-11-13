using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces.Secrets;
using System.Net;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Secrets;

/// <summary>
/// Reads secrets from a HashiCorp Vault KV version 2 engine.
/// </summary>
/// <remarks>
/// A reference names a path and, after <c>#</c>, a key: <c>{{secret:vault:apps/mail#password}}</c> reads key
/// <c>password</c> of <c>{mount}/data/apps/mail</c>. Without a key, <see cref="VaultSecretsOptions.DefaultKey"/>
/// is read. Authentication is by token (configured, read from a file, or <c>VAULT_TOKEN</c>).
/// </remarks>
public class VaultSecretProvider(IHttpClientFactory httpClientFactory, IOptionsMonitor<SecretsOptions> options) : ISecretProvider
{
    /// <summary>
    /// Provider name used in configuration and explicit references.
    /// </summary>
    public const string ProviderName = "vault";

    /// <summary>
    /// Name of the HTTP client registered for this provider.
    /// </summary>
    public const string HttpClientName = "milvaion-secrets-vault";

    /// <inheritdoc/>
    public string Name => ProviderName;

    /// <inheritdoc/>
    public async Task<string> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        var vault = options.CurrentValue.Vault;

        if (vault == null || string.IsNullOrWhiteSpace(vault.Address))
            return null;

        var (path, key) = SplitName(name, vault.DefaultKey);

        if (string.IsNullOrWhiteSpace(path))
            return null;

        var token = ReadToken(vault);

        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Vault is configured without a token");

        var mount = (vault.MountPath ?? "secret").Trim('/');
        var escapedPath = string.Join('/', path.Trim('/').Split('/').Select(Uri.EscapeDataString));
        var url = $"{vault.Address.TrimEnd('/')}/v1/{mount}/data/{escapedPath}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.Add("X-Vault-Token", token);

        if (!string.IsNullOrWhiteSpace(vault.Namespace))
            request.Headers.Add("X-Vault-Namespace", vault.Namespace);

        var client = httpClientFactory.CreateClient(HttpClientName);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, vault.TimeoutSeconds)));

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Vault did not answer in time");
        }
        catch (HttpRequestException ex)
        {
            // The exception text can carry the address but never the token or a value.
            throw new InvalidOperationException($"Vault could not be reached ({ex.HttpRequestError})");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Vault returned HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            if (!document.RootElement.TryGetProperty("data", out var outer)
                || outer.ValueKind != JsonValueKind.Object
                || !outer.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(key, out var value))
                return null;

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => value.GetRawText()
            };
        }
    }

    /// <summary>
    /// Splits <c>path#key</c>. Without <c>#</c>, the key is <paramref name="defaultKey"/>.
    /// </summary>
    public static (string Path, string Key) SplitName(string name, string defaultKey)
    {
        var hash = name.LastIndexOf('#');

        if (hash < 0)
            return (name, string.IsNullOrWhiteSpace(defaultKey) ? "value" : defaultKey);

        return (name[..hash], name[(hash + 1)..]);
    }

    private static string ReadToken(VaultSecretsOptions vault)
    {
        if (!string.IsNullOrWhiteSpace(vault.Token))
            return vault.Token.Trim();

        if (!string.IsNullOrWhiteSpace(vault.TokenFile) && File.Exists(vault.TokenFile))
            return File.ReadAllText(vault.TokenFile).Trim();

        return System.Environment.GetEnvironmentVariable("VAULT_TOKEN")?.Trim();
    }
}
