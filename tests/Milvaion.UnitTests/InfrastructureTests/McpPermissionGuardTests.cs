using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Milvaion.Api.Mcp;
using Milvaion.Application.Utils.PermissionManager;
using ModelContextProtocol;
using System.Security.Claims;

namespace Milvaion.UnitTests.InfrastructureTests;

/// <summary>
/// MCP tools are not MVC actions, so this guard is the only per-permission check in front of them.
/// </summary>
[Trait("Infrastructure Unit Tests", "Unit tests for McpPermissionGuard.")]
public class McpPermissionGuardTests
{
    private const string _permission = PermissionCatalog.ScheduledJobManagement.List;

    [Fact]
    public void Require_WhenNotAuthenticated_ShouldThrow()
    {
        var guard = CreateGuard(new ClaimsPrincipal(new ClaimsIdentity()));

        var act = () => guard.Require(_permission);

        act.Should().Throw<McpException>().WithMessage("Not authenticated*");
    }

    [Fact]
    public void Require_WithoutHttpContext_ShouldThrow()
    {
        var guard = new McpPermissionGuard(new HttpContextAccessor());

        var act = () => guard.Require(_permission);

        act.Should().Throw<McpException>();
    }

    [Fact]
    public void Require_WithOtherPermissionOnly_ShouldThrowAndNameTheMissingPermission()
    {
        var guard = CreateGuard(Authenticated(PermissionCatalog.UserManagement.List));

        var act = () => guard.Require(_permission);

        act.Should().Throw<McpException>().WithMessage($"*'{_permission}'*");
    }

    [Theory]
    [InlineData(_permission)]
    [InlineData(PermissionCatalog.App.SuperAdmin)]
    public void Require_WithPermissionOrSuperAdmin_ShouldNotThrow(string held)
    {
        var guard = CreateGuard(Authenticated(held));

        var act = () => guard.Require(_permission);

        act.Should().NotThrow();
    }

    [Fact]
    public void Has_ShouldReflectHeldPermissions()
    {
        CreateGuard(Authenticated(_permission)).Has(_permission).Should().BeTrue();
        CreateGuard(Authenticated(PermissionCatalog.UserManagement.List)).Has(_permission).Should().BeFalse();
        CreateGuard(new ClaimsPrincipal(new ClaimsIdentity())).Has(_permission).Should().BeFalse();
    }

    private static ClaimsPrincipal Authenticated(params string[] roles) => new(new ClaimsIdentity(roles.Select(role => new Claim(ClaimTypes.Role, role)), "Test"));

    private static McpPermissionGuard CreateGuard(ClaimsPrincipal user) => new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
}
