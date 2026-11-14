using Milvasoft.Milvaion.Sdk.Domain;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// The state of the current project context.
/// </summary>
public enum ProjectScopeKind
{
    /// <summary>
    /// Inside an HTTP request whose project has not been resolved (yet). Queries over project data return nothing
    /// and writes of project data are rejected: forgetting to resolve a project fails closed.
    /// </summary>
    Unresolved = 0,

    /// <summary>
    /// The request runs in one project.
    /// </summary>
    Resolved = 1,

    /// <summary>
    /// Background services and explicitly entered system scopes. Sees every project.
    /// </summary>
    System = 2
}

/// <summary>
/// The project the current code runs in. Read by the EF query filter, the save checks and project-aware services.
/// </summary>
/// <remarks>
/// Outside an HTTP request (background services) the context is <see cref="ProjectScopeKind.System"/>. Inside a request
/// it is <see cref="ProjectScopeKind.Unresolved"/> until the project has been resolved for that request.
/// </remarks>
public interface IProjectContext
{
    /// <summary>
    /// Current state.
    /// </summary>
    ProjectScopeKind Kind { get; }

    /// <summary>
    /// Id of the resolved project. -1 when <see cref="Kind"/> is not <see cref="ProjectScopeKind.Resolved"/>, a value that matches no row.
    /// </summary>
    int ProjectId { get; }

    /// <summary>
    /// Slug of the resolved project, or null.
    /// </summary>
    string ProjectSlug { get; }

    /// <summary>
    /// True when the query filter must not restrict rows: in a system scope, and in a resolved request while projects
    /// are disabled (every row then belongs to the Default project, so the filter would only cost query time).
    /// </summary>
    bool BypassFilter { get; }

    /// <summary>
    /// True for system scopes and for requests in the Default project. Resources that are not yet split by project -
    /// the worker registry and the instance-wide counters - are visible only then.
    /// </summary>
    bool CanSeeDefaultProjectResources => Kind == ProjectScopeKind.System || (Kind == ProjectScopeKind.Resolved && ProjectId == ProjectDefaults.DefaultProjectId);

    /// <summary>
    /// Enters a scope that sees every project until the returned value is disposed. Use only for code that must work
    /// across projects or before the request's project is known, such as authenticating an api key.
    /// </summary>
    IDisposable EnterSystemScope();

    /// <summary>
    /// Runs the following code as if it were a request in <paramref name="projectId"/> until the returned value is
    /// disposed. Background services use it before calling request handlers (for example a cron workflow trigger), so
    /// the handler stamps and filters by the right project.
    /// </summary>
    IDisposable EnterProjectScope(int projectId);
}
