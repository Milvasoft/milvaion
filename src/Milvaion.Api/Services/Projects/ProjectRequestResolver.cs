using Microsoft.Extensions.Options;
using Milvaion.Api.Utils;
using Milvaion.Application.Utils.Projects;
using System.Globalization;
using System.Security.Claims;

namespace Milvaion.Api.Services.Projects;

/// <summary>
/// The project of a request and the project permissions its caller holds there.
/// </summary>
/// <param name="Resolution">Which project the request runs in, or why it cannot run.</param>
/// <param name="KeepTokenProjectPermissions">
/// True when the project permissions already in the principal apply: always for api keys (they belong to one project)
/// and super admins, and for users in the Default project, where the project permissions of their roles apply.
/// </param>
/// <param name="AddedPermissions">Permissions granted by the caller's membership in the project.</param>
public sealed record ProjectRequestOutcome(ProjectResolution Resolution, bool KeepTokenProjectPermissions, IReadOnlyList<string> AddedPermissions);

/// <summary>
/// Decides which project a request runs in and what the caller may do there. See docs/githubdocs/PROJECTS-DESIGN.md.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>An api key runs in its own project. A project header naming another project is rejected.</item>
/// <item>A user names the project with the <c>X-Milvaion-Project</c> header (or <c>?project=</c> for SignalR). Without
/// one the request runs in the Default project unless <see cref="ProjectsOptions.RequireExplicitProject"/> is on.</item>
/// <item>Super admins may enter every project. Other users need a membership, except in the Default project.</item>
/// <item>Instance permissions always apply. Project permissions of a user's roles apply in the Default project only;
/// in other projects only the membership's role counts. This is decision 1 of the design.</item>
/// </list>
/// </remarks>
public sealed class ProjectRequestResolver(IProjectDirectory directory, IOptionsMonitor<ProjectsOptions> options)
{
    private readonly IProjectDirectory _directory = directory;
    private readonly IOptionsMonitor<ProjectsOptions> _options = options;

    /// <summary>
    /// The Default project resolution, used while projects are disabled and for anonymous requests.
    /// </summary>
    /// <remarks>Declared before <see cref="DisabledOutcome"/>, which reads it: static initializers run in order.</remarks>
    public static ProjectResolution DefaultResolution { get; } = new(ProjectDefaults.DefaultProjectId, ProjectDefaults.DefaultProjectSlug, false);

    /// <summary>
    /// The outcome while projects are disabled: every request runs in the Default project with its permissions unchanged.
    /// </summary>
    public static ProjectRequestOutcome DisabledOutcome { get; } = new(DefaultResolution, true, []);

    /// <summary>
    /// Resolves the project of the request for <paramref name="principal"/>.
    /// </summary>
    public async Task<ProjectRequestOutcome> ResolveAsync(HttpContext httpContext, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        if (!options.Enabled)
            return DisabledOutcome;

        var requestedSlug = GetRequestedSlug(httpContext);

        var apiKeyProjectClaim = principal.FindFirst(ApiKeyAuthenticationDefaults.ApiKeyProjectIdClaimName)?.Value;

        if (principal.HasClaim(c => c.Type == ApiKeyAuthenticationDefaults.ApiKeyIdClaimName))
            return await ResolveApiKeyAsync(apiKeyProjectClaim, requestedSlug, cancellationToken);

        if (requestedSlug is null && options.RequireExplicitProject)
            return Failed(ProjectResolutionError.ProjectRequired);

        var project = await _directory.FindBySlugAsync(requestedSlug ?? ProjectDefaults.DefaultProjectSlug, cancellationToken);

        if (project is null)
            return Failed(ProjectResolutionError.ProjectNotFound);

        var resolution = new ProjectResolution(project.Id, project.Slug, project.IsArchived);
        var isDefault = project.Id == ProjectDefaults.DefaultProjectId;

        if (principal.IsInRole(PermissionCatalog.App.SuperAdmin))
            return new ProjectRequestOutcome(resolution, true, []);

        var userId = await GetUserIdAsync(principal, cancellationToken);

        var membershipPermissions = userId is null ? null : await _directory.GetMembershipPermissionsAsync(userId.Value, project.Id, cancellationToken);

        if (membershipPermissions is null && !isDefault)
            return Failed(ProjectResolutionError.NotMember);

        return new ProjectRequestOutcome(resolution, isDefault, membershipPermissions ?? []);
    }

    private async Task<ProjectRequestOutcome> ResolveApiKeyAsync(string apiKeyProjectClaim, string requestedSlug, CancellationToken cancellationToken)
    {
        var projectId = int.TryParse(apiKeyProjectClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : ProjectDefaults.DefaultProjectId;

        var project = await _directory.FindByIdAsync(projectId, cancellationToken);

        if (project is null)
            return Failed(ProjectResolutionError.ProjectNotFound);

        if (requestedSlug is not null && !string.Equals(requestedSlug, project.Slug, StringComparison.OrdinalIgnoreCase))
            return Failed(ProjectResolutionError.ApiKeyProjectMismatch);

        return new ProjectRequestOutcome(new ProjectResolution(project.Id, project.Slug, project.IsArchived), true, []);
    }

    private async Task<int?> GetUserIdAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (int.TryParse(principal.FindFirst(MilvaionClaimTypes.UserId)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
            return userId;

        // Tokens issued before the user id claim existed carry only the user name.
        return await _directory.FindUserIdAsync(principal.Identity?.Name, cancellationToken);
    }

    private static string GetRequestedSlug(HttpContext httpContext)
    {
        var value = httpContext.Request.Headers[ProjectResolution.HeaderName].ToString();

        if (string.IsNullOrWhiteSpace(value))
            value = httpContext.Request.Query[ProjectResolution.QueryName].ToString();

        return string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    }

    private static ProjectRequestOutcome Failed(ProjectResolutionError error) => new(ProjectResolution.Failed(error), false, []);
}
