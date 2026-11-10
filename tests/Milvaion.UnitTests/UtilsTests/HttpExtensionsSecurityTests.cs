using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Milvaion.Application.Features.Configuration.GetSystemConfiguration;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.Extensions;
using Milvaion.Application.Utils.PermissionManager;
using Milvasoft.Core.Exceptions;
using System.Net;
using System.Security.Claims;

namespace Milvaion.UnitTests.UtilsTests;

public class HttpExtensionsSecurityTests
{
    private static DefaultHttpContext ContextWith(params string[] permissions)
        => new() { User = new ClaimsPrincipal(new ClaimsIdentity(permissions.Select(p => new Claim(ClaimTypes.Role, p)), "Test")) };

    [Fact]
    public void ThrowIfCurrentUserNotAuthorized_WithRequiredPermission_ShouldPass()
    {
        var act = () => ContextWith("Users.List").ThrowIfCurrentUserNotAuthorized(["Users.List"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfCurrentUserNotAuthorized_WithSuperAdmin_ShouldPass()
    {
        var act = () => ContextWith(PermissionCatalog.App.SuperAdmin).ThrowIfCurrentUserNotAuthorized(["Users.List"]);

        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfCurrentUserNotAuthorized_WithoutPermission_ShouldForbid()
    {
        var context = ContextWith("Other.List");

        var act = () => context.ThrowIfCurrentUserNotAuthorized(["Users.List"]);

        act.Should().Throw<MilvaUserFriendlyException>();
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void ThrowIfCurrentUserNotAuthorized_ShouldNotModifyTheRequiredList()
    {
        List<string> required = ["Users.List"];

        ContextWith("Users.List").ThrowIfCurrentUserNotAuthorized(required);

        required.Should().Equal("Users.List");
    }

    [Theory]
    [InlineData("10.0.0.5", "203.0.113.9", "203.0.113.9")]
    [InlineData("172.18.0.2", "203.0.113.9", "203.0.113.9")]
    [InlineData("192.168.1.1", "203.0.113.9", "203.0.113.9")]
    [InlineData("127.0.0.1", "203.0.113.9", "203.0.113.9")]
    [InlineData("198.51.100.7", "203.0.113.9", "198.51.100.7")]
    [InlineData("10.0.0.5", "not-an-ip", "10.0.0.5")]
    public void GetIpAddress_ShouldTrustRealIpOnlyFromLocalNetwork(string remoteAddress, string header, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
        context.Request.Headers[GlobalConstant.RealIpHeaderKey] = header;

        context.GetIpAddress().Should().Be(expected);
    }

    [Theory]
    [InlineData("redis:6379,password=secret,ssl=true", "redis:6379")]
    [InlineData("a:6379,b:6379,password=secret", "a:6379,b:6379")]
    [InlineData("redis://user:secret@host:6379", "host:6379")]
    [InlineData("localhost:6379", "localhost:6379")]
    [InlineData("", "")]
    public void GetRedisEndpoints_ShouldNotExposeCredentials(string connectionString, string expected)
        => GetSystemConfigurationQueryHandler.GetRedisEndpoints(connectionString).Should().Be(expected);

    [Theory]
    [InlineData("abc", "%abc%")]
    [InlineData(" abc ", "%abc%")]
    [InlineData("100%", "%100\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("a\\b", "%a\\\\b%")]
    public void ToContainsPattern_ShouldTreatTheTermLiterally(string term, string expected)
        => term.ToContainsPattern().Should().Be(expected);

    [Theory]
    [InlineData(5L, 5)]
    [InlineData(long.MaxValue, int.MaxValue)]
    [InlineData(long.MinValue, int.MinValue)]
    public void ToSaturatedInt_ShouldStopAtTheLimit(long value, int expected)
        => value.ToSaturatedInt().Should().Be(expected);
}
