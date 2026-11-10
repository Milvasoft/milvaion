using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Milvaion.Api.AppStartup;
using Milvaion.Api.Controllers;
using Milvaion.Api.Utils;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Infrastructure.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Reflection;
using System.Text;

namespace Milvaion.UnitTests.UtilsTests;

[Trait("Utils Unit Tests", "Security related helpers.")]
public class SecurityHardeningTests
{
    private static readonly byte[] _secret = Encoding.ASCII.GetBytes(new string('k', 64));

    [Fact]
    public void GenerateApiKey_WithoutExpiry_ShouldNotCarryAnExpiry()
    {
        var key = KeyHelper.GenerateApiKey(_secret, 7, 1, null);

        new JwtSecurityTokenHandler().ReadJwtToken(key).Payload.Expiration.Should().BeNull();
        KeyHelper.ValidateApiKey(key, _secret).Should().NotBeNull();
    }

    [Fact]
    public void GenerateApiKey_WithExpiry_ShouldRoundTripTheExpiry()
    {
        var expiresAt = DateTime.UtcNow.AddDays(1);

        var result = KeyHelper.ValidateApiKey(KeyHelper.GenerateApiKey(_secret, 7, 1, expiresAt), _secret);

        result.ApiKeyId.Should().Be(7);
        result.ExpiresAt.Should().BeCloseTo(expiresAt, TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("*", @"\2a")]
    [InlineData("a)(uid=*", @"a\29\28uid=\2a")]
    [InlineData(@"back\slash", @"back\5cslash")]
    public void EscapeFilterValue_ShouldNeutralizeFilterSyntax(string value, string expected)
        => LdapAuthenticator.EscapeFilterValue(value).Should().Be(expected);

    [Theory]
    [InlineData("alice", "alice")]
    [InlineData("a,ou=admins", @"a\,ou\=admins")]
    [InlineData("#lead", @"\#lead")]
    [InlineData(" edge ", @"\ edge\ ")]
    public void EscapeDnValue_ShouldNeutralizeDnSyntax(string value, string expected)
        => LdapAuthenticator.EscapeDnValue(value).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CHANGE_ME_SET_Something")]
    public void EnsureSigningSecrets_WhenUnset_ShouldGenerateWithoutThrowing(string configured)
    {
        var configuration = BuildConfiguration(configured);

        var generated = configuration.EnsureSigningSecrets();

        generated.Should().BeEquivalentTo(StartupExtensions.JwtSigningKeyPath, StartupExtensions.ApiKeySecretPath);
        configuration[StartupExtensions.JwtSigningKeyPath].Should().HaveLength(64).And.NotBe(configured);
        configuration[StartupExtensions.ApiKeySecretPath].Should().NotBe(configuration[StartupExtensions.JwtSigningKeyPath]);
    }

    [Fact]
    public void EnsureSigningSecrets_WhenConfigured_ShouldKeepTheValues()
    {
        var configuration = BuildConfiguration("deployment-value");

        configuration.EnsureSigningSecrets().Should().BeEmpty();

        configuration[StartupExtensions.JwtSigningKeyPath].Should().Be("deployment-value");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DeveloperEndpointsFeatureProvider_ShouldOnlyKeepControllerWhenEnabled(bool enabled, bool expected)
    {
        var feature = new ControllerFeature();
        feature.Controllers.Add(typeof(DeveloperController).GetTypeInfo());

        new DeveloperEndpointsFeatureProvider(enabled).PopulateFeature([], feature);

        feature.Controllers.Contains(typeof(DeveloperController).GetTypeInfo()).Should().Be(expected);
    }

    [Fact]
    public void GetIpAddress_ShouldIgnoreClientSuppliedHeader()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Real-IP"] = "203.0.113.9";
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.4");

        context.GetIpAddress().Should().Be("198.51.100.4");
    }

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("Basic abc", null)]
    public void GetTokenFromHeader_ShouldOnlyAcceptBearer(string header, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = header;

        context.GetTokenFromHeader().Should().Be(expected);
    }

    private static ConfigurationManager BuildConfiguration(string value)
    {
        var configuration = new ConfigurationManager();

        configuration.AddInMemoryCollection(new Dictionary<string, string>
        {
            [StartupExtensions.JwtSigningKeyPath] = value,
            [StartupExtensions.ApiKeySecretPath] = value
        });

        return configuration;
    }
}
