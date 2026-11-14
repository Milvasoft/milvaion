using Milvaion.Application.Interfaces;

namespace Milvaion.UnitTests.InfrastructureTests.Projects;

/// <summary>
/// A fixed project context for tests.
/// </summary>
internal sealed class FakeProjectContext(ProjectScopeKind kind, int projectId = -1, bool bypassFilter = false) : IProjectContext
{
    public ProjectScopeKind Kind { get; } = kind;

    public int ProjectId { get; } = kind == ProjectScopeKind.Resolved ? projectId : -1;

    public string ProjectSlug => null;

    public bool BypassFilter { get; } = bypassFilter || kind == ProjectScopeKind.System;

    public IDisposable EnterSystemScope() => throw new NotSupportedException();

    public IDisposable EnterProjectScope(int projectId) => throw new NotSupportedException();

    public static FakeProjectContext Resolved(int projectId) => new(ProjectScopeKind.Resolved, projectId);

    public static FakeProjectContext System() => new(ProjectScopeKind.System);

    public static FakeProjectContext Unresolved() => new(ProjectScopeKind.Unresolved);
}
