namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// An entity that belongs to exactly one project. The API only returns rows of the project a request runs in.
/// </summary>
/// <remarks>
/// Worker code never creates these entities, so the property has no effect on workers.
/// See docs/githubdocs/PROJECTS-DESIGN.md.
/// </remarks>
public interface IProjectScoped
{
    /// <summary>
    /// Id of the owning project. <see cref="ProjectDefaults.DefaultProjectId"/> for data created before projects existed.
    /// </summary>
    int ProjectId { get; set; }
}

/// <summary>
/// An entity that usually belongs to one project, but may also be instance-wide (for example an instance api key).
/// Instance-wide rows have a null <see cref="ProjectId"/> and are not visible inside any project.
/// </summary>
public interface IOptionalProjectScoped
{
    /// <summary>
    /// Id of the owning project, or null for an instance-wide row.
    /// </summary>
    int? ProjectId { get; set; }
}

/// <summary>
/// Well-known project values.
/// </summary>
public static class ProjectDefaults
{
    /// <summary>
    /// Id of the Default project. Every row that existed before projects were introduced belongs to it.
    /// </summary>
    public const int DefaultProjectId = 1;

    /// <summary>
    /// Slug of the Default project.
    /// </summary>
    public const string DefaultProjectSlug = "default";
}
