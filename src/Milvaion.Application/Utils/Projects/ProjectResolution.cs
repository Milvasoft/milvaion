namespace Milvaion.Application.Utils.Projects;

/// <summary>
/// Outcome of resolving the project of an HTTP request. Stored in <c>HttpContext.Items</c> under <see cref="ItemKey"/>.
/// </summary>
/// <param name="ProjectId">Resolved project id, or 0 when <paramref name="Error"/> is set.</param>
/// <param name="ProjectSlug">Resolved project slug.</param>
/// <param name="IsArchived">True when the project is archived and only reads are allowed.</param>
/// <param name="Error">Why the request cannot run in a project, or null.</param>
public sealed record ProjectResolution(int ProjectId, string ProjectSlug, bool IsArchived, ProjectResolutionError? Error = null)
{
    /// <summary>
    /// Key of the resolution in <c>HttpContext.Items</c>.
    /// </summary>
    public const string ItemKey = "Milvaion.ProjectResolution";

    /// <summary>
    /// Header that names the project of a request.
    /// </summary>
    public const string HeaderName = "X-Milvaion-Project";

    /// <summary>
    /// Query string parameter that names the project, for the SignalR handshake where browsers cannot set headers.
    /// </summary>
    public const string QueryName = "project";

    /// <summary>
    /// True when the request may run.
    /// </summary>
    public bool Succeeded => Error is null;

    /// <summary>
    /// A failed resolution.
    /// </summary>
    public static ProjectResolution Failed(ProjectResolutionError error) => new(0, null, false, error);
}

/// <summary>
/// Reasons a request cannot run in the project it asked for.
/// </summary>
public enum ProjectResolutionError
{
    /// <summary>
    /// No project with the given slug exists. Answered with 404.
    /// </summary>
    ProjectNotFound,

    /// <summary>
    /// The caller is not a member of the project. Answered with 403.
    /// </summary>
    NotMember,

    /// <summary>
    /// An api key bound to one project was used with a header naming another. Answered with 403.
    /// </summary>
    ApiKeyProjectMismatch,

    /// <summary>
    /// The project was not named while <c>RequireExplicitProject</c> is on. Answered with 400.
    /// </summary>
    ProjectRequired,

    /// <summary>
    /// A write was attempted in an archived project. Answered with 403.
    /// </summary>
    ProjectArchived
}
