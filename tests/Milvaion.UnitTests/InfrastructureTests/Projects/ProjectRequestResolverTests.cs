using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Milvaion.Api.Services.Projects;
using Milvaion.Api.Utils;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Application.Utils.Projects;
using Moq;
using System.Security.Claims;

namespace Milvaion.UnitTests.InfrastructureTests.Projects;

/// <summary>
/// Which project a request runs in and what its caller may do there.
/// </summary>
[Trait("Category", "Unit")]
public class ProjectRequestResolverTests
{
    private const int _paymentsId = 2;
    private const int _billingId = 3;
    private const int _memberUserId = 42;

    private static readonly ProjectInfo _default = new(1, "default", false);
    private static readonly ProjectInfo _payments = new(_paymentsId, "payments", false);
    private static readonly ProjectInfo _billing = new(_billingId, "billing", false);

    private static ProjectRequestResolver CreateResolver(bool enabled = true, bool requireExplicit = false)
    {
        var directory = new Mock<IProjectDirectory>();

        foreach (var project in new[] { _default, _payments, _billing })
        {
            directory.Setup(d => d.FindBySlugAsync(project.Slug, It.IsAny<CancellationToken>())).ReturnsAsync(project);
            directory.Setup(d => d.FindByIdAsync(project.Id, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        }

        directory.Setup(d => d.GetMembershipPermissionsAsync(_memberUserId, _paymentsId, It.IsAny<CancellationToken>()))
                 .ReturnsAsync([PermissionCatalog.ScheduledJobManagement.List]);
        directory.Setup(d => d.FindUserIdAsync("legacy", It.IsAny<CancellationToken>())).ReturnsAsync(_memberUserId);

        var options = Mock.Of<IOptionsMonitor<ProjectsOptions>>(o => o.CurrentValue == new ProjectsOptions { Enabled = enabled, RequireExplicitProject = requireExplicit });

        return new ProjectRequestResolver(directory.Object, options);
    }

    private static HttpContext Request(string projectHeader = null)
    {
        var http = new DefaultHttpContext();

        if (projectHeader is not null)
            http.Request.Headers[ProjectResolution.HeaderName] = projectHeader;

        return http;
    }

    private static ClaimsPrincipal User(int? userId, string name = "alice", params string[] permissions)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };

        if (userId is not null)
            claims.Add(new Claim(MilvaionClaimTypes.UserId, userId.Value.ToString()));

        claims.AddRange(permissions.Select(p => new Claim(ClaimTypes.Role, p)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal ApiKey(int? projectId, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, "apikey:ci"),
            new(ApiKeyAuthenticationDefaults.ApiKeyIdClaimName, "7")
        };

        if (projectId is not null)
            claims.Add(new Claim(ApiKeyAuthenticationDefaults.ApiKeyProjectIdClaimName, projectId.Value.ToString()));

        claims.AddRange(permissions.Select(p => new Claim(ClaimTypes.Role, p)));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeyAuthenticationDefaults.AuthenticationScheme));
    }

    [Fact]
    public async Task Disabled_EveryRequestRunsInDefault_AndTheHeaderIsIgnored()
    {
        var outcome = await CreateResolver(enabled: false).ResolveAsync(Request("payments"), User(1), default);

        outcome.Resolution.ProjectId.Should().Be(1);
        outcome.KeepTokenProjectPermissions.Should().BeTrue();
        outcome.AddedPermissions.Should().BeEmpty();
    }

    [Fact]
    public async Task NoHeader_RunsInDefault_WithTheRolesProjectPermissions()
    {
        var outcome = await CreateResolver().ResolveAsync(Request(), User(5), default);

        outcome.Resolution.Should().Be(new ProjectResolution(1, "default", false));
        outcome.KeepTokenProjectPermissions.Should().BeTrue();
    }

    [Fact]
    public async Task NoHeader_IsRejected_WhenAnExplicitProjectIsRequired()
    {
        var outcome = await CreateResolver(requireExplicit: true).ResolveAsync(Request(), User(5), default);

        outcome.Resolution.Error.Should().Be(ProjectResolutionError.ProjectRequired);
    }

    [Fact]
    public async Task UnknownProject_IsNotFound()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("nope"), User(5), default);

        outcome.Resolution.Error.Should().Be(ProjectResolutionError.ProjectNotFound);
    }

    [Fact]
    public async Task NonMember_IsRejected()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("billing"), User(_memberUserId), default);

        outcome.Resolution.Error.Should().Be(ProjectResolutionError.NotMember);
    }

    [Fact]
    public async Task Member_GetsOnlyTheMembershipPermissions()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("Payments"), User(_memberUserId), default);

        outcome.Resolution.ProjectId.Should().Be(_paymentsId);
        outcome.KeepTokenProjectPermissions.Should().BeFalse("the project permissions of global roles apply in the Default project only");
        outcome.AddedPermissions.Should().Equal(PermissionCatalog.ScheduledJobManagement.List);
    }

    [Fact]
    public async Task TokenWithoutUserIdClaim_IsMatchedByUserName()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("payments"), User(null, "legacy"), default);

        outcome.Resolution.ProjectId.Should().Be(_paymentsId);
    }

    [Fact]
    public async Task SuperAdmin_EntersEveryProject_WithoutMembership()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("billing"), User(1, "root", PermissionCatalog.App.SuperAdmin), default);

        outcome.Resolution.ProjectId.Should().Be(_billingId);
        outcome.KeepTokenProjectPermissions.Should().BeTrue();
    }

    [Fact]
    public async Task ApiKey_RunsInItsOwnProject()
    {
        var outcome = await CreateResolver().ResolveAsync(Request(), ApiKey(_paymentsId), default);

        outcome.Resolution.ProjectId.Should().Be(_paymentsId);
        outcome.KeepTokenProjectPermissions.Should().BeTrue();
    }

    [Fact]
    public async Task ApiKey_WithMatchingHeader_IsAccepted()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("payments"), ApiKey(_paymentsId), default);

        outcome.Resolution.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task ApiKey_CannotBePointedAtAnotherProject()
    {
        var outcome = await CreateResolver().ResolveAsync(Request("billing"), ApiKey(_paymentsId), default);

        outcome.Resolution.Error.Should().Be(ProjectResolutionError.ApiKeyProjectMismatch);
    }

    [Fact]
    public async Task ApiKey_CachedBeforeProjects_BelongsToDefault()
    {
        var outcome = await CreateResolver().ResolveAsync(Request(), ApiKey(null), default);

        outcome.Resolution.ProjectId.Should().Be(1);
    }

    [Fact]
    public void Rebuild_DropsProjectPermissions_KeepsInstanceOnes_AndAddsMembership()
    {
        var principal = User(5, "alice", PermissionCatalog.UserManagement.List, PermissionCatalog.ScheduledJobManagement.Delete);

        var rebuilt = ProjectClaimsTransformation.Rebuild(principal, keepProjectPermissions: false, [PermissionCatalog.ScheduledJobManagement.List]);

        rebuilt.IsInRole(PermissionCatalog.UserManagement.List).Should().BeTrue();
        rebuilt.IsInRole(PermissionCatalog.ScheduledJobManagement.Delete).Should().BeFalse();
        rebuilt.IsInRole(PermissionCatalog.ScheduledJobManagement.List).Should().BeTrue();
        rebuilt.Identity!.Name.Should().Be("alice");
        rebuilt.Identity.IsAuthenticated.Should().BeTrue();
    }

    [Fact]
    public void PermissionScopes_ClassifyTheCatalog()
    {
        PermissionScopes.IsProjectPermission(PermissionCatalog.ScheduledJobManagement.Trigger).Should().BeTrue();
        PermissionScopes.IsProjectPermission(PermissionCatalog.WorkflowManagement.List).Should().BeTrue();
        PermissionScopes.IsProjectPermission(PermissionCatalog.ExclusionCalendarManagement.Create).Should().BeTrue();
        PermissionScopes.IsProjectPermission(PermissionCatalog.ApiKeyManagement.Create).Should().BeTrue();
        PermissionScopes.IsProjectPermission(PermissionCatalog.App.SuperAdmin).Should().BeFalse();
        PermissionScopes.IsProjectPermission(PermissionCatalog.UserManagement.List).Should().BeFalse();
        PermissionScopes.IsProjectPermission(PermissionCatalog.SystemAdministration.List).Should().BeFalse();
        PermissionScopes.IsProjectPermission("Unknown.Thing").Should().BeFalse();
        PermissionScopes.IsProjectPermission(null).Should().BeFalse();
    }
}
