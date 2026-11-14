namespace Milvaion.Application.Utils.Models.Options;

/// <summary>
/// Project (multi-tenancy) settings. Bound from <c>MilvaionConfig:Projects</c>. See docs/githubdocs/PROJECTS-DESIGN.md.
/// </summary>
public class ProjectsOptions
{
    /// <summary>
    /// Section key in configuration files.
    /// </summary>
    public const string SectionKey = "MilvaionConfig:Projects";

    /// <summary>
    /// When false (the default) every request runs in the Default project, the project header is ignored and
    /// permissions come from roles exactly as before projects existed. When true the API resolves the project of each
    /// request and evaluates project permissions per project.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// When true and projects are enabled, a request authenticated as a user must name its project in the
    /// <c>X-Milvaion-Project</c> header instead of falling back to the Default project. Default false.
    /// </summary>
    public bool RequireExplicitProject { get; set; }

    /// <summary>
    /// How long project and membership lookups are cached in memory, in seconds. Default 30.
    /// </summary>
    public int CacheSeconds { get; set; } = 30;
}
