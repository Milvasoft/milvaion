using FluentAssertions;
using HttpWorker.Options;
using HttpWorker.Security;
using Microsoft.Extensions.Options;
using Milvasoft.Milvaion.Sdk.Worker.Exceptions;
using System.Net;

namespace Milvaion.UnitTests.WorkerTests;

[Trait("Worker Unit Tests", "HttpWorker target policy.")]
public class HttpTargetPolicyTests
{
    private static HttpTargetPolicy CreatePolicy(Action<HttpWorkerSecurityOptions> configure = null)
    {
        var options = new HttpWorkerOptions();

        configure?.Invoke(options.Security);

        return new HttpTargetPolicy(Microsoft.Extensions.Options.Options.Create(options));
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://intranet.local/")]
    public void Defaults_ShouldAllowEveryTargetAndFeature(string url)
    {
        var policy = CreatePolicy();

        policy.RestrictsTargets.Should().BeFalse();
        policy.Invoking(p => p.EnsureUrlAllowed(url)).Should().NotThrow();
        policy.Invoking(p => p.EnsureJobSettingsAllowed(ignoreSslErrors: true, usesProxy: true, usesThumbprintCertificate: true)).Should().NotThrow();
    }

    [Fact]
    public void Apply_ShouldLeaveHandlerUntouched_WhenNoRuleIsConfigured()
    {
        using var handler = new SocketsHttpHandler();

        CreatePolicy().Apply(handler);

        handler.ConnectCallback.Should().BeNull();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("::ffff:10.0.0.1")]
    public void BlockPrivateNetworks_ShouldRefuseInternalAddresses(string address)
        => CreatePolicy(s => s.BlockPrivateNetworks = true).IsAddressAllowed(IPAddress.Parse(address)).Should().BeFalse();

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void BlockPrivateNetworks_ShouldAllowPublicAddresses(string address)
        => CreatePolicy(s => s.BlockPrivateNetworks = true).IsAddressAllowed(IPAddress.Parse(address)).Should().BeTrue();

    [Fact]
    public void AllowedCidrs_ShouldOverrideBlockedRanges()
    {
        var policy = CreatePolicy(s =>
        {
            s.BlockPrivateNetworks = true;
            s.AllowedCidrs = ["10.20.0.0/16"];
        });

        policy.IsAddressAllowed(IPAddress.Parse("10.20.1.1")).Should().BeTrue();
        policy.IsAddressAllowed(IPAddress.Parse("10.21.1.1")).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://api.example.com/x", true)]
    [InlineData("https://API.EXAMPLE.COM/x", true)]
    [InlineData("https://a.partner.org/x", true)]
    [InlineData("https://partner.org/x", false)]
    [InlineData("https://evilpartner.org/x", false)]
    [InlineData("https://other.com/x", false)]
    public void AllowedHosts_ShouldMatchExactAndWildcardNames(string url, bool allowed)
    {
        var policy = CreatePolicy(s => s.AllowedHosts = ["api.example.com", "*.partner.org"]);

        if (allowed)
            policy.Invoking(p => p.EnsureUrlAllowed(url)).Should().NotThrow();
        else
            policy.Invoking(p => p.EnsureUrlAllowed(url)).Should().Throw<PermanentJobException>();
    }

    [Fact]
    public void EnsureUrlAllowed_ShouldRefuseLiteralInternalAddress()
        => CreatePolicy(s => s.BlockPrivateNetworks = true).Invoking(p => p.EnsureUrlAllowed("http://[::1]:8080/")).Should().Throw<PermanentJobException>();

    [Fact]
    public void Constructor_ShouldFail_WhenCidrIsInvalid()
        => FluentActions.Invoking(() => CreatePolicy(s => s.BlockedCidrs = ["not-a-cidr"])).Should().Throw<InvalidOperationException>().WithMessage("*not-a-cidr*");

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void EnsureJobSettingsAllowed_ShouldRefuseDisabledFeatures(bool ignoreSsl, bool proxy, bool thumbprint)
    {
        var policy = CreatePolicy(s =>
        {
            s.AllowIgnoreSslErrors = false;
            s.AllowProxy = false;
            s.AllowThumbprintCertificates = false;
        });

        policy.Invoking(p => p.EnsureJobSettingsAllowed(ignoreSsl, proxy, thumbprint)).Should().Throw<PermanentJobException>();
    }

    [Fact]
    public async Task ConnectCallback_ShouldRefuseConnection_SoRedirectsAreCoveredToo()
    {
        using var handler = new SocketsHttpHandler();

        CreatePolicy(s => s.BlockPrivateNetworks = true).Apply(handler);

        using var client = new HttpClient(handler);

        // localhost is a name, so unlike a literal address it only gets refused at connect time
        var act = () => client.GetAsync("http://localhost:9/");

        var exception = await act.Should().ThrowAsync<HttpRequestException>();

        exception.Which.InnerException.Should().BeOfType<HttpTargetBlockedException>();
    }
}
