using Azure;
using Azure.Security.KeyVault.Secrets;
using FluentAssertions;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.Services.Secrets;
using Moq;
using System.Net;
using System.Text;

namespace Milvaion.UnitTests.InfrastructureTests.Secrets;

[Trait("Infrastructure Unit Tests", "Built-in secret providers.")]
public class SecretProviderTests
{
    #region Environment

    [Theory]
    [InlineData("smtp-password", "MILVAION_SECRET_SMTP_PASSWORD")]
    [InlineData("apps/db.main", "MILVAION_SECRET_APPS_DB_MAIN")]
    [InlineData("Already_Upper9", "MILVAION_SECRET_ALREADY_UPPER9")]
    public void EnvironmentProvider_ShouldMapNamesToVariables(string name, string expected)
        => EnvironmentSecretProvider.ToVariableName("MILVAION_SECRET_", name).Should().Be(expected);

    [Fact]
    public async Task EnvironmentProvider_ShouldReadTheVariable()
    {
        var name = $"unit-{Guid.NewGuid():N}";
        var variable = EnvironmentSecretProvider.ToVariableName("MILVAION_SECRET_", name);
        var provider = new EnvironmentSecretProvider(new TestOptionsMonitor<SecretsOptions>(new SecretsOptions()));

        try
        {
            Environment.SetEnvironmentVariable(variable, "from-env");

            (await provider.GetSecretAsync(name)).Should().Be("from-env");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }

        (await provider.GetSecretAsync(name)).Should().BeNull();
    }

    #endregion

    #region Configuration

    [Fact]
    public async Task ConfigurationProvider_ShouldReadValuesCaseInsensitively()
    {
        var options = new SecretsOptions { Values = new Dictionary<string, string> { ["Smtp-Password"] = "from-config" } };
        var provider = new ConfigurationSecretProvider(new TestOptionsMonitor<SecretsOptions>(options));

        (await provider.GetSecretAsync("smtp-password")).Should().Be("from-config");
        (await provider.GetSecretAsync("other")).Should().BeNull();
    }

    #endregion

    #region Vault

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (VaultSecretProvider Provider, StubHandler Handler) CreateVault(Func<HttpRequestMessage, HttpResponseMessage> respond, Action<VaultSecretsOptions> configure = null)
    {
        var options = new SecretsOptions
        {
            Vault = new VaultSecretsOptions { Address = "https://vault.test:8200/", Token = "s.test-token", MountPath = "/kv/" }
        };

        configure?.Invoke(options.Vault);

        var handler = new StubHandler(respond);

        return (new VaultSecretProvider(new StubHttpClientFactory(handler), new TestOptionsMonitor<SecretsOptions>(options)), handler);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string _kvResponse = """{"data":{"data":{"password":"vault-pw","value":"default-value","port":5432},"metadata":{"version":3}}}""";

    [Fact]
    public async Task VaultProvider_ShouldReadTheKeyFromTheKvV2Path()
    {
        var (provider, handler) = CreateVault(_ => Json(_kvResponse), v => v.Namespace = "team-a");

        var value = await provider.GetSecretAsync("apps/mail server#password");

        value.Should().Be("vault-pw");

        var request = handler.Requests.Single();
        request.RequestUri!.AbsoluteUri.Should().Be("https://vault.test:8200/v1/kv/data/apps/mail%20server");
        request.Headers.GetValues("X-Vault-Token").Should().Equal("s.test-token");
        request.Headers.GetValues("X-Vault-Namespace").Should().Equal("team-a");
    }

    [Fact]
    public async Task VaultProvider_WithoutKey_ShouldReadTheDefaultKey()
    {
        var (provider, _) = CreateVault(_ => Json(_kvResponse));

        (await provider.GetSecretAsync("apps/mail")).Should().Be("default-value");
    }

    [Fact]
    public async Task VaultProvider_WithNonStringValue_ShouldReturnItsJson()
    {
        var (provider, _) = CreateVault(_ => Json(_kvResponse));

        (await provider.GetSecretAsync("apps/db#port")).Should().Be("5432");
    }

    [Fact]
    public async Task VaultProvider_WhenPathOrKeyIsMissing_ShouldReturnNull()
    {
        var (notFound, _) = CreateVault(_ => Json("""{"errors":[]}""", HttpStatusCode.NotFound));
        var (noKey, _) = CreateVault(_ => Json(_kvResponse));

        (await notFound.GetSecretAsync("apps/none#password")).Should().BeNull();
        (await noKey.GetSecretAsync("apps/mail#nope")).Should().BeNull();
    }

    [Fact]
    public async Task VaultProvider_WhenAccessIsDenied_ShouldThrowWithoutTheToken()
    {
        var (provider, _) = CreateVault(_ => Json("""{"errors":["permission denied"]}""", HttpStatusCode.Forbidden));

        var act = () => provider.GetSecretAsync("apps/mail#password");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("403").And.NotContain("s.test-token");
    }

    [Fact]
    public async Task VaultProvider_WhenNotConfigured_ShouldReturnNullWithoutCalling()
    {
        var (provider, handler) = CreateVault(_ => Json(_kvResponse), v => v.Address = null);

        (await provider.GetSecretAsync("apps/mail#password")).Should().BeNull();
        handler.Requests.Should().BeEmpty();
    }

    #endregion

    #region Azure Key Vault

    private static AzureKeyVaultSecretProvider CreateAzure(Mock<SecretClient> client, string vaultUri = "https://unit.vault.azure.net/")
    {
        var options = new SecretsOptions { AzureKeyVault = new AzureKeyVaultSecretsOptions { VaultUri = vaultUri } };

        return new AzureKeyVaultSecretProvider(new TestOptionsMonitor<SecretsOptions>(options), _ => client.Object);
    }

    [Fact]
    public async Task AzureKeyVaultProvider_ShouldReadTheSecretAndVersion()
    {
        var client = new Mock<SecretClient>();
        client.Setup(c => c.GetSecretAsync("smtp-password", "v2", It.IsAny<CancellationToken>()))
              .ReturnsAsync(Response.FromValue(SecretModelFactory.KeyVaultSecret(new SecretProperties("smtp-password"), "kv-pw"), Mock.Of<Response>()));

        var provider = CreateAzure(client);

        (await provider.GetSecretAsync("smtp-password/v2")).Should().Be("kv-pw");
    }

    [Fact]
    public async Task AzureKeyVaultProvider_WhenSecretDoesNotExist_ShouldReturnNull()
    {
        var client = new Mock<SecretClient>();
        client.Setup(c => c.GetSecretAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new RequestFailedException(404, "SecretNotFound"));

        (await CreateAzure(client).GetSecretAsync("absent")).Should().BeNull();
    }

    [Fact]
    public async Task AzureKeyVaultProvider_WhenForbidden_ShouldThrowAShortMessage()
    {
        var client = new Mock<SecretClient>();
        client.Setup(c => c.GetSecretAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new RequestFailedException(403, "Caller is not authorized: details"));

        var act = () => CreateAzure(client).GetSecretAsync("smtp-password");

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("Azure Key Vault returned HTTP 403");
    }

    [Fact]
    public async Task AzureKeyVaultProvider_WhenNotConfigured_ShouldReturnNull()
    {
        var client = new Mock<SecretClient>(MockBehavior.Strict);

        (await CreateAzure(client, vaultUri: null).GetSecretAsync("smtp-password")).Should().BeNull();
    }

    #endregion
}
