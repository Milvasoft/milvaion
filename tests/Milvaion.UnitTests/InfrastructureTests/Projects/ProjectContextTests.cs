using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Models.Options;
using Milvaion.Application.Utils.Projects;
using Milvaion.Infrastructure.Services.Projects;
using Moq;

namespace Milvaion.UnitTests.InfrastructureTests.Projects;

[Trait("Category", "Unit")]
public class ProjectContextTests
{
    private static (IProjectContext Context, DefaultHttpContext Http) Create(bool enabled, bool withRequest = true)
    {
        var http = withRequest ? new DefaultHttpContext() : null;
        var accessor = new HttpContextAccessor { HttpContext = http };
        var options = Mock.Of<IOptionsMonitor<ProjectsOptions>>(o => o.CurrentValue == new ProjectsOptions { Enabled = enabled });

        return (new ProjectContext(accessor, options), http);
    }

    [Fact]
    public void OutsideARequest_IsSystem()
    {
        var (context, _) = Create(enabled: true, withRequest: false);

        context.Kind.Should().Be(ProjectScopeKind.System);
        context.BypassFilter.Should().BeTrue();
    }

    [Fact]
    public void RequestWithoutResolution_IsUnresolved_AndMatchesNothing()
    {
        var (context, _) = Create(enabled: true);

        context.Kind.Should().Be(ProjectScopeKind.Unresolved);
        context.ProjectId.Should().Be(-1);
        context.BypassFilter.Should().BeFalse();
        context.CanSeeDefaultProjectResources.Should().BeFalse();
    }

    [Fact]
    public void FailedResolution_IsUnresolved()
    {
        var (context, http) = Create(enabled: true);
        http.Items[ProjectResolution.ItemKey] = ProjectResolution.Failed(ProjectResolutionError.NotMember);

        context.Kind.Should().Be(ProjectScopeKind.Unresolved);
    }

    [Fact]
    public void ResolvedRequest_FiltersByItsProject_WhenEnabled()
    {
        var (context, http) = Create(enabled: true);
        http.Items[ProjectResolution.ItemKey] = new ProjectResolution(3, "payments", false);

        context.Kind.Should().Be(ProjectScopeKind.Resolved);
        context.ProjectId.Should().Be(3);
        context.ProjectSlug.Should().Be("payments");
        context.BypassFilter.Should().BeFalse();
        context.CanSeeDefaultProjectResources.Should().BeFalse();
    }

    [Fact]
    public void ResolvedRequest_BypassesTheFilter_WhenDisabled()
    {
        var (context, http) = Create(enabled: false);
        http.Items[ProjectResolution.ItemKey] = new ProjectResolution(1, "default", false);

        context.BypassFilter.Should().BeTrue();
        context.CanSeeDefaultProjectResources.Should().BeTrue();
    }

    [Fact]
    public async Task SystemScope_AppliesUntilDisposed_AndOnlyToItsFlow()
    {
        var (context, _) = Create(enabled: true);

        using (context.EnterSystemScope())
        {
            context.Kind.Should().Be(ProjectScopeKind.System);

            // A child flow inherits it.
            (await Task.Run(() => context.Kind)).Should().Be(ProjectScopeKind.System);
        }

        context.Kind.Should().Be(ProjectScopeKind.Unresolved);
    }

    [Fact]
    public void ProjectScope_ActsAsAResolvedRequest()
    {
        var (context, _) = Create(enabled: true, withRequest: false);

        using (context.EnterProjectScope(9))
        {
            context.Kind.Should().Be(ProjectScopeKind.Resolved);
            context.ProjectId.Should().Be(9);
            context.BypassFilter.Should().BeFalse();

            using (context.EnterSystemScope())
                context.Kind.Should().Be(ProjectScopeKind.System);

            context.ProjectId.Should().Be(9);
        }

        context.Kind.Should().Be(ProjectScopeKind.System);
    }

    [Fact]
    public void ProjectScope_RejectsAnInvalidProject()
    {
        var (context, _) = Create(enabled: true);

        var act = () => context.EnterProjectScope(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
