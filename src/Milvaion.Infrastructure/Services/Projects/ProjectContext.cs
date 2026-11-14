using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Projects;

namespace Milvaion.Infrastructure.Services.Projects;

/// <summary>
/// Default <see cref="IProjectContext"/>. Stateless apart from an async-local system scope counter, so it is registered
/// as a singleton and can be used from singletons such as the worker registry.
/// </summary>
/// <remarks>
/// The state is derived on every read:
/// <list type="number">
/// <item>Inside <see cref="EnterSystemScope"/>: <see cref="ProjectScopeKind.System"/>.</item>
/// <item>No HTTP request (background services): <see cref="ProjectScopeKind.System"/>.</item>
/// <item>The request's <see cref="ProjectResolution"/> succeeded: <see cref="ProjectScopeKind.Resolved"/>.</item>
/// <item>Otherwise: <see cref="ProjectScopeKind.Unresolved"/>, which matches no rows.</item>
/// </list>
/// </remarks>
public sealed class ProjectContext(IHttpContextAccessor httpContextAccessor, IOptionsMonitor<ProjectsOptions> options) : IProjectContext
{
    private static readonly AsyncLocal<int> _systemDepth = new();
    private static readonly AsyncLocal<int?> _projectOverride = new();

    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly IOptionsMonitor<ProjectsOptions> _options = options;

    /// <inheritdoc/>
    public ProjectScopeKind Kind
    {
        get
        {
            if (_systemDepth.Value > 0)
                return ProjectScopeKind.System;

            if (_projectOverride.Value is not null)
                return ProjectScopeKind.Resolved;

            var httpContext = _httpContextAccessor.HttpContext;

            if (httpContext is null)
                return ProjectScopeKind.System;

            return GetResolution(httpContext) is { Succeeded: true } ? ProjectScopeKind.Resolved : ProjectScopeKind.Unresolved;
        }
    }

    /// <inheritdoc/>
    public int ProjectId => Kind == ProjectScopeKind.Resolved ? _projectOverride.Value ?? GetResolution(_httpContextAccessor.HttpContext).ProjectId : -1;

    /// <inheritdoc/>
    public string ProjectSlug => Kind == ProjectScopeKind.Resolved && _projectOverride.Value is null ? GetResolution(_httpContextAccessor.HttpContext).ProjectSlug : null;

    /// <inheritdoc/>
    public bool BypassFilter
    {
        get
        {
            var kind = Kind;

            return kind == ProjectScopeKind.System || (kind == ProjectScopeKind.Resolved && !_options.CurrentValue.Enabled);
        }
    }

    /// <inheritdoc/>
    public IDisposable EnterSystemScope()
    {
        _systemDepth.Value++;

        return new SystemScope();
    }

    /// <inheritdoc/>
    public IDisposable EnterProjectScope(int projectId)
    {
        if (projectId <= 0)
            throw new ArgumentOutOfRangeException(nameof(projectId), projectId, "A project scope needs a real project id.");

        var previous = _projectOverride.Value;
        var previousDepth = _systemDepth.Value;

        _projectOverride.Value = projectId;
        _systemDepth.Value = 0;

        return new Restore(() =>
        {
            _projectOverride.Value = previous;
            _systemDepth.Value = previousDepth;
        });
    }

    private static ProjectResolution GetResolution(HttpContext httpContext)
        => httpContext?.Items.TryGetValue(ProjectResolution.ItemKey, out var value) == true ? value as ProjectResolution : null;

    private sealed class Restore(Action restore) : IDisposable
    {
        private Action _restore = restore;

        public void Dispose()
        {
            _restore?.Invoke();
            _restore = null;
        }
    }

    private sealed class SystemScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _systemDepth.Value--;
        }
    }
}
