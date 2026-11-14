using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Milvaion.Application.Interfaces;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Milvaion.Sdk.Domain;

namespace Milvaion.UnitTests.InfrastructureTests.Projects;

/// <summary>
/// The checks every SaveChanges applies to project data. No database is involved: the rules only read the change tracker.
/// </summary>
[Trait("Category", "Unit")]
public class ProjectSaveRulesTests
{
    private static MilvaionDbContext NewContext() => new(new DbContextOptionsBuilder<MilvaionDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    private static ScheduledJob NewJob(int projectId = 0) => new() { Id = Guid.CreateVersion7(), ProjectId = projectId, DisplayName = "job", JobNameInWorker = "Job" };

    [Fact]
    public void Added_InResolvedRequest_TakesTheRequestProject()
    {
        using var context = NewContext();
        var job = NewJob();
        context.Add(job);

        ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Resolved(7));

        job.ProjectId.Should().Be(7);
    }

    [Fact]
    public void Added_WithAnotherProject_InResolvedRequest_IsRejected()
    {
        using var context = NewContext();
        context.Add(NewJob(projectId: 8));

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Resolved(7));

        act.Should().Throw<ProjectScopeViolationException>();
    }

    [Fact]
    public void Added_InUnresolvedRequest_IsRejected()
    {
        using var context = NewContext();
        context.Add(NewJob(projectId: 1));

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Unresolved());

        act.Should().Throw<ProjectScopeViolationException>();
    }

    [Fact]
    public void Added_InSystemScope_WithoutProject_IsRejected()
    {
        using var context = NewContext();
        context.Add(NewJob());

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.System());

        act.Should().Throw<ProjectScopeViolationException>().WithMessage("*copy ProjectId from the parent row*");
    }

    [Fact]
    public void Added_InSystemScope_WithProject_IsKept()
    {
        using var context = NewContext();
        var job = NewJob(projectId: 3);
        context.Add(job);

        ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.System());

        job.ProjectId.Should().Be(3);
    }

    [Fact]
    public void Added_WithoutProjectContext_JoinsTheDefaultProject()
    {
        using var context = NewContext();
        var job = NewJob();
        context.Add(job);

        ProjectSaveRules.Apply(context.ChangeTracker, null);

        job.ProjectId.Should().Be(ProjectDefaults.DefaultProjectId);
    }

    [Fact]
    public void Updated_FromAProjection_NeverWritesAMissingProject()
    {
        using var context = NewContext();
        var job = NewJob();
        context.Update(job);

        ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Resolved(1));

        context.Entry(job).Property(j => j.ProjectId).IsModified.Should().BeFalse();
    }

    [Fact]
    public void Updated_MovingToAnotherProject_IsRejected()
    {
        using var context = NewContext();
        var job = NewJob(projectId: 1);
        context.Attach(job);
        job.ProjectId = 2;

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.System());

        act.Should().Throw<ProjectScopeViolationException>().WithMessage("*cannot be moved*");
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public void ChangingARowOfAnotherProject_InResolvedRequest_IsRejected(EntityState state)
    {
        using var context = NewContext();
        var job = NewJob(projectId: 2);
        context.Entry(job).State = state;

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Resolved(1));

        act.Should().Throw<ProjectScopeViolationException>();
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public void ChangingProjectData_InUnresolvedRequest_IsRejected(EntityState state)
    {
        using var context = NewContext();
        var job = NewJob(projectId: 1);
        context.Entry(job).State = state;

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Unresolved());

        act.Should().Throw<ProjectScopeViolationException>();
    }

    [Fact]
    public void OptionalProject_Added_InResolvedRequest_TakesTheRequestProject()
    {
        using var context = NewContext();
        var apiKey = new MilvaionApiKey { Name = "key" };
        context.Add(apiKey);

        ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Resolved(4));

        apiKey.ProjectId.Should().Be(4);
    }

    [Fact]
    public void OptionalProject_Added_InSystemScope_MayStayInstanceWide()
    {
        using var context = NewContext();
        var apiKey = new MilvaionApiKey { Name = "key" };
        context.Add(apiKey);

        ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.System());

        apiKey.ProjectId.Should().BeNull();
    }

    [Fact]
    public void OptionalProject_Updated_NeverNullsOutTheProject()
    {
        using var context = NewContext();
        var apiKey = new MilvaionApiKey { Id = 5, Name = "key" };
        context.Update(apiKey);

        ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Resolved(1));

        context.Entry(apiKey).Property(k => k.ProjectId).IsModified.Should().BeFalse();
    }

    [Fact]
    public void EntitiesOutsideProjects_AreLeftAlone()
    {
        using var context = NewContext();
        context.Add(new User { UserName = "u" });

        var act = () => ProjectSaveRules.Apply(context.ChangeTracker, FakeProjectContext.Unresolved());

        act.Should().NotThrow();
    }
}
