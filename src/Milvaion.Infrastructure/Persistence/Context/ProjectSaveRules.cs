using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Milvaion.Application.Interfaces;

namespace Milvaion.Infrastructure.Persistence.Context;

/// <summary>
/// Thrown when a save would put project data into the wrong project or move it between projects.
/// It signals a programming error, never a user error, so it is not translated into a friendly message.
/// </summary>
public sealed class ProjectScopeViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Project checks applied to every <c>SaveChanges</c>. Bulk operations bypass <c>SaveChanges</c>; their callers set
/// <c>ProjectId</c> themselves, copied from the parent row.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Added rows get the current project when they have none, and are rejected when they name another project.</item>
/// <item>In a system scope added rows must already carry their project (copied from their parent).</item>
/// <item>A context without a project context (created outside dependency injection) puts rows without a project into the Default project.</item>
/// <item>Inside a request whose project is unresolved, project rows cannot be added at all.</item>
/// <item>An update never writes a missing project id (an entity loaded through a projection) and never moves a row.</item>
/// <item>Inside a request, rows of another project cannot be updated or deleted.</item>
/// </list>
/// </remarks>
public static class ProjectSaveRules
{
    private const string _propertyName = nameof(IProjectScoped.ProjectId);

    /// <summary>
    /// Applies the rules to the tracked changes.
    /// </summary>
    /// <param name="changeTracker">Change tracker of the context being saved.</param>
    /// <param name="projectContext">Project context of the context, or null for a context created outside dependency injection.</param>
    public static void Apply(ChangeTracker changeTracker, IProjectContext projectContext)
    {
        foreach (var entry in changeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case IProjectScoped:
                    ApplyRequired(entry, projectContext);
                    break;
                case IOptionalProjectScoped:
                    ApplyOptional(entry, projectContext);
                    break;
            }
        }
    }

    private static void ApplyRequired(EntityEntry entry, IProjectContext projectContext)
    {
        var property = entry.Property(_propertyName);
        var current = (int)property.CurrentValue!;
        var kind = projectContext?.Kind ?? ProjectScopeKind.System;

        switch (entry.State)
        {
            case EntityState.Added:
                // A context created outside dependency injection (migrations, seeding, test setup) has no project
                // context: rows it creates without a project join the Default project, like all data before projects.
                if (projectContext is null)
                {
                    if (current == 0)
                        property.CurrentValue = ProjectDefaults.DefaultProjectId;

                    break;
                }

                if (kind == ProjectScopeKind.Unresolved)
                    throw Violation(entry, "cannot be created before the request's project is resolved");

                if (kind == ProjectScopeKind.Resolved)
                {
                    if (current == 0)
                        property.CurrentValue = projectContext.ProjectId;
                    else if (current != projectContext.ProjectId)
                        throw Violation(entry, $"belongs to project {current} but the request runs in project {projectContext.ProjectId}");
                }
                else if (current == 0)
                    throw Violation(entry, "has no project. Code running outside a request must copy ProjectId from the parent row");

                break;

            case EntityState.Modified:
                if (property.IsModified)
                {
                    var original = (int)property.OriginalValue!;

                    // An entity attached from a projection carries 0: never write that.
                    if (current == 0)
                        property.IsModified = false;
                    else if (original != 0 && original != current)
                        throw Violation(entry, $"cannot be moved from project {original} to project {current}");
                }

                EnsureSameProject(entry, projectContext, current);
                break;

            case EntityState.Deleted:
                EnsureSameProject(entry, projectContext, current);
                break;
        }
    }

    private static void ApplyOptional(EntityEntry entry, IProjectContext projectContext)
    {
        var property = entry.Property(_propertyName);
        var current = (int?)property.CurrentValue;
        var kind = projectContext?.Kind ?? ProjectScopeKind.System;

        switch (entry.State)
        {
            case EntityState.Added:
                if (kind == ProjectScopeKind.Resolved)
                {
                    if (current is null)
                        property.CurrentValue = projectContext.ProjectId;
                    else if (current != projectContext.ProjectId)
                        throw Violation(entry, $"belongs to project {current} but the request runs in project {projectContext.ProjectId}");
                }

                break;

            case EntityState.Modified:
                if (property.IsModified)
                {
                    var original = (int?)property.OriginalValue;

                    // Never null out a project through an update, and never move a row.
                    if (current is null)
                        property.IsModified = false;
                    else if (original is not null && original != current)
                        throw Violation(entry, $"cannot be moved from project {original} to project {current}");
                }

                EnsureSameProject(entry, projectContext, current ?? 0);
                break;

            case EntityState.Deleted:
                EnsureSameProject(entry, projectContext, current ?? 0);
                break;
        }
    }

    private static void EnsureSameProject(EntityEntry entry, IProjectContext projectContext, int current)
    {
        if (projectContext?.Kind == ProjectScopeKind.Unresolved)
            throw Violation(entry, "cannot be changed before the request's project is resolved");

        if (projectContext?.Kind == ProjectScopeKind.Resolved && current != 0 && current != projectContext.ProjectId)
            throw Violation(entry, $"belongs to project {current} and cannot be changed from project {projectContext.ProjectId}");
    }

    private static ProjectScopeViolationException Violation(EntityEntry entry, string reason)
        => new($"{entry.Metadata.ClrType.Name} {reason}.");
}
