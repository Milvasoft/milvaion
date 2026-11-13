using FluentAssertions;
using Milvaion.Application.Interfaces.Secrets;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Infrastructure.Services.Secrets;
using System.Text.Json;

namespace Milvaion.UnitTests.InfrastructureTests.Secrets;

[Trait("Infrastructure Unit Tests", "Secret reference resolution.")]
public class SecretResolverTests
{
    private readonly FakeSecretProvider _env = new("env");
    private readonly FakeSecretProvider _config = new("config");
    private readonly FakeSecretProvider _vault = new("vault");
    private readonly TestOptionsMonitor<SecretsOptions> _options = new(new SecretsOptions());
    private readonly ManualTimeProvider _time = new();
    private readonly SecretMasker _masker;
    private readonly SecretResolver _resolver;

    public SecretResolverTests()
    {
        _masker = new SecretMasker(_options, _time);
        _resolver = new SecretResolver([_env, _config, _vault], _options, _masker, timeProvider: _time);
    }

    [Fact]
    public async Task ResolveAsync_WithoutReferences_ShouldReturnTheSameText()
    {
        const string jobData = """{"to":"user@example.com","secretary":"not a reference"}""";

        var result = await _resolver.ResolveAsync(jobData);

        result.Should().BeSameAs(jobData);
        _env.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ResolveAsync_WithEmptyText_ShouldReturnIt(string jobData)
    {
        var result = await _resolver.ResolveAsync(jobData);

        result.Should().Be(jobData);
    }

    [Fact]
    public async Task ResolveAsync_ShouldReplaceWholeAndPartialStringReferences()
    {
        _env.Values["smtp-password"] = "hunter2!";
        _config.Values["api-token"] = "tok_123";

        var result = await _resolver.ResolveAsync("""{"password":"{{secret:smtp-password}}","auth":"Bearer {{ secret:api-token }}"}""");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("password").GetString().Should().Be("hunter2!");
        doc.RootElement.GetProperty("auth").GetString().Should().Be("Bearer tok_123");
    }

    [Fact]
    public async Task ResolveAsync_ShouldEscapeValuesSoTheDocumentStaysValidJson()
    {
        const string nasty = "a\"b\\c\nd</script>ü";
        _env.Values["nasty"] = nasty;

        var result = await _resolver.ResolveAsync("""{"value":"{{secret:nasty}}","other":1}""");

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("value").GetString().Should().Be(nasty);
        doc.RootElement.GetProperty("other").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_ShouldResolveTheSameReferenceOnce()
    {
        _env.Values["db"] = "pw";

        var result = await _resolver.ResolveAsync("""{"a":"{{secret:db}}","b":"{{secret:db}}"}""");

        result.Should().Be("""{"a":"pw","b":"pw"}""");
        _env.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ResolveAsync_ShouldUseTheFirstProviderThatHasTheSecret()
    {
        _config.Values["shared"] = "from-config";
        _vault.Values["shared"] = "from-vault";

        var result = await _resolver.ResolveAsync("""{"v":"{{secret:shared}}"}""");

        result.Should().Be("""{"v":"from-config"}""");
        _env.Requests.Should().Equal("shared");
        _vault.Requests.Should().BeEmpty("vault is not in the default provider order");
    }

    [Fact]
    public async Task ResolveAsync_ShouldFollowTheConfiguredProviderOrder()
    {
        _options.CurrentValue.Providers = ["vault", "config"];
        _config.Values["shared"] = "from-config";
        _vault.Values["shared"] = "from-vault";

        var result = await _resolver.ResolveAsync("""{"v":"{{secret:shared}}"}""");

        result.Should().Be("""{"v":"from-vault"}""");
    }

    [Theory]
    [InlineData("vault:apps/mail#password")]
    [InlineData("hashicorpvault:apps/mail#password")]
    public async Task ResolveAsync_WithExplicitProvider_ShouldAskOnlyThatProvider(string reference)
    {
        _vault.Values["apps/mail#password"] = "vault-pw";
        _env.Values["apps/mail#password"] = "wrong";

        var result = await _resolver.ResolveAsync($$$"""{"v":"{{secret:{{{reference}}}}}"}""");

        result.Should().Be("""{"v":"vault-pw"}""");
        _env.Requests.Should().BeEmpty();
        _vault.Requests.Should().Equal("apps/mail#password");
    }

    [Fact]
    public async Task ResolveAsync_WithUnknownPrefix_ShouldTreatTheWholeTextAsTheName()
    {
        _env.Values["team:token"] = "t";

        var result = await _resolver.ResolveAsync("""{"v":"{{secret:team:token}}"}""");

        result.Should().Be("""{"v":"t"}""");
    }

    [Fact]
    public async Task ResolveAsync_WithMissingSecret_ShouldThrowNamingItWithoutLeakingResolvedValues()
    {
        _env.Values["present"] = "super-secret-value";

        var act = () => _resolver.ResolveAsync("""{"a":"{{secret:present}}","b":"{{secret:absent}}","c":"{{secret:also-absent}}"}""");

        var ex = (await act.Should().ThrowAsync<SecretResolutionException>()).Which;

        ex.MissingSecrets.Should().BeEquivalentTo(["absent", "also-absent"]);
        ex.Message.Should().Contain("absent").And.Contain("also-absent").And.Contain("not published");
        ex.Message.Should().NotContain("super-secret-value");
    }

    [Fact]
    public async Task ResolveAsync_WhenProviderFails_ShouldReportItWithoutTheExceptionText()
    {
        _options.CurrentValue.Providers = ["vault"];
        _vault.FailWith = new HttpRequestException("token=s.abcdef leaked in a message");

        var act = () => _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""");

        var ex = (await act.Should().ThrowAsync<SecretResolutionException>()).Which;

        ex.FailedSecrets.Should().ContainKey("db");
        ex.Message.Should().Contain("vault").And.Contain(nameof(HttpRequestException));
        ex.Message.Should().NotContain("s.abcdef");
    }

    [Fact]
    public async Task ResolveAsync_WhenFirstProviderFailsButNextHasTheSecret_ShouldSucceed()
    {
        _env.FailWith = new InvalidOperationException("env broken");
        _config.Values["db"] = "pw";

        var result = await _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""");

        result.Should().Be("""{"a":"pw"}""");
    }

    [Fact]
    public async Task ResolveAsync_ShouldCacheValuesForTheConfiguredTtl()
    {
        _options.CurrentValue.CacheTtlSeconds = 30;
        _env.Values["db"] = "first";

        await _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""");

        _env.Values["db"] = "rotated";
        _time.Advance(TimeSpan.FromSeconds(29));

        (await _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""")).Should().Be("""{"a":"first"}""");

        _time.Advance(TimeSpan.FromSeconds(2));

        (await _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""")).Should().Be("""{"a":"rotated"}""");
        _env.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_WithZeroTtl_ShouldNotCache()
    {
        _options.CurrentValue.CacheTtlSeconds = 0;
        _env.Values["db"] = "pw";

        await _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""");
        await _resolver.ResolveAsync("""{"a":"{{secret:db}}"}""");

        _env.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task ResolveAsync_ShouldNotCacheMisses()
    {
        var act = () => _resolver.ResolveAsync("""{"a":"{{secret:late}}"}""");
        await act.Should().ThrowAsync<SecretResolutionException>();

        _env.Values["late"] = "now-here";

        (await _resolver.ResolveAsync("""{"a":"{{secret:late}}"}""")).Should().Be("""{"a":"now-here"}""");
    }

    [Fact]
    public async Task ResolveAsync_WhenDisabled_ShouldLeaveReferencesAlone()
    {
        _options.CurrentValue.Enabled = false;
        const string jobData = """{"a":"{{secret:db}}"}""";

        (await _resolver.ResolveAsync(jobData)).Should().Be(jobData);
        _env.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolveAsync_ShouldRegisterResolvedValuesForMasking()
    {
        _env.Values["smtp-password"] = "hunter2!";

        await _resolver.ResolveAsync("""{"p":"{{secret:smtp-password}}"}""");

        _masker.Mask("login failed for password hunter2!").Should().Be("login failed for password {{secret:smtp-password}}");
    }

    [Theory]
    [InlineData("""{"a":"{{secret:x}}"}""", new[] { "x" })]
    [InlineData("""{"a":"{{ secret:vault:apps/db#pw }}","b":"{{secret:x}} {{secret:x}}"}""", new[] { "vault:apps/db#pw", "x" })]
    [InlineData("""{"a":"{{secret:}}","b":"{secret:x}","c":"{{secrets:x}}","d":"{{secret:has space}}"}""", new string[0])]
    public void SecretReferences_Find_ShouldParseTheSyntax(string text, string[] expected)
    {
        SecretReferences.Find(text).Should().Equal(expected);
        SecretReferences.ContainsReference(text).Should().Be(expected.Length > 0);
    }
}
