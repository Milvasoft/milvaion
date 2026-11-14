using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Milvaion.Sdk.Domain;
using System.Reflection;

namespace Milvaion.UnitTests.InfrastructureTests.Projects;

/// <summary>
/// Guards the data model: an entity that carries a project must be filtered by it.
/// </summary>
[Trait("Category", "Unit")]
public class ProjectModelTests
{
    private static MilvaionDbContext NewContext() => new(new DbContextOptionsBuilder<MilvaionDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    [Fact]
    public void EveryEntityWithAProjectId_ImplementsAProjectInterface()
    {
        using var context = NewContext();

        var offenders = context.Model.GetEntityTypes()
                                     .Where(t => t.FindProperty("ProjectId") is not null)
                                     .Where(t => t.ClrType != typeof(ProjectMembership))
                                     .Where(t => !typeof(IProjectScoped).IsAssignableFrom(t.ClrType) && !typeof(IOptionalProjectScoped).IsAssignableFrom(t.ClrType))
                                     .Select(t => t.ClrType.Name)
                                     .ToList();

        offenders.Should().BeEmpty("a ProjectId column without the interface would not be filtered");
    }

    [Fact]
    public void EveryProjectEntity_HasTheProjectQueryFilter()
    {
        using var context = NewContext();

        var projectEntities = context.Model.GetEntityTypes()
                                           .Where(t => typeof(IProjectScoped).IsAssignableFrom(t.ClrType) || typeof(IOptionalProjectScoped).IsAssignableFrom(t.ClrType))
                                           .ToList();

        projectEntities.Select(t => t.ClrType).Should().Contain([typeof(ScheduledJob), typeof(JobOccurrence), typeof(JobOccurrenceLog), typeof(FailedOccurrence),
                                                                  typeof(Workflow), typeof(WorkflowRun), typeof(MetricReport), typeof(ExclusionCalendar),
                                                                  typeof(MilvaionApiKey), typeof(ActivityLog)]);

        foreach (var entityType in projectEntities)
            entityType.GetDeclaredQueryFilters().Select(f => f.Key).Should().Contain(MilvaionDbContext.ProjectFilterName, $"{entityType.ClrType.Name} must be filtered by project");
    }

    [Fact]
    public void ScopedEntities_OfTheSdk_AreTheExpectedOnes()
    {
        var scoped = typeof(ScheduledJob).Assembly.GetTypes()
                                                  .Where(t => t.IsClass && !t.IsAbstract && typeof(IProjectScoped).IsAssignableFrom(t))
                                                  .Select(t => t.Name)
                                                  .ToList();

        scoped.Should().BeEquivalentTo([nameof(ScheduledJob), nameof(JobOccurrence), nameof(JobOccurrenceLog), nameof(FailedOccurrence),
                                        nameof(Workflow), nameof(WorkflowRun), nameof(MetricReport)]);
    }

    [Fact]
    public void FilterReadsTheContextPerQuery()
    {
        // The filter must reference members of the context, not values captured when the model was built.
        var bypass = typeof(MilvaionDbContext).GetProperty(nameof(MilvaionDbContext.ProjectFilterBypass), BindingFlags.Public | BindingFlags.Instance);
        var id = typeof(MilvaionDbContext).GetProperty(nameof(MilvaionDbContext.ProjectFilterId), BindingFlags.Public | BindingFlags.Instance);

        bypass.Should().NotBeNull();
        id.Should().NotBeNull();

        using var context = NewContext();

        context.ProjectFilterBypass.Should().BeTrue("a context created outside dependency injection sees every project");
        context.ProjectFilterId.Should().Be(-1);

        context.ProjectContext = FakeProjectContext.Unresolved();
        context.ProjectFilterBypass.Should().BeFalse();
        context.ProjectFilterId.Should().Be(-1);

        context.ProjectContext = FakeProjectContext.Resolved(5);
        context.ProjectFilterBypass.Should().BeFalse();
        context.ProjectFilterId.Should().Be(5);
    }
}
