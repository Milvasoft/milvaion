using System.Collections.Frozen;
using System.Reflection;

namespace Milvaion.Application.Utils.PermissionManager;

/// <summary>
/// Where a permission applies.
/// </summary>
public enum PermissionScopeKind
{
    /// <summary>
    /// Applies to the whole installation: users, roles, system administration, content.
    /// </summary>
    Instance = 0,

    /// <summary>
    /// Applies inside one project: jobs, workflows, failures, workers, calendars, api keys, activity.
    /// </summary>
    Project = 1
}

/// <summary>
/// Marks a permission group in <see cref="PermissionCatalog"/> with its scope. Groups without it are instance groups.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class PermissionScopeAttribute(PermissionScopeKind scope) : Attribute
{
    /// <summary>
    /// The scope of every permission in the group.
    /// </summary>
    public PermissionScopeKind Scope { get; } = scope;
}

/// <summary>
/// Looks up the scope of a permission string such as <c>ScheduledJobManagement.List</c>.
/// </summary>
public static class PermissionScopes
{
    private static readonly FrozenSet<string> _projectGroups = typeof(PermissionCatalog).GetNestedTypes()
                                                                                         .Where(t => t.GetCustomAttribute<PermissionScopeAttribute>()?.Scope == PermissionScopeKind.Project)
                                                                                         .Select(t => t.Name)
                                                                                         .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Names of the permission groups that apply inside a project.
    /// </summary>
    public static IReadOnlyCollection<string> ProjectGroups => _projectGroups;

    /// <summary>
    /// Returns the scope of <paramref name="permission"/>. Unknown permissions are treated as instance permissions.
    /// </summary>
    public static PermissionScopeKind GetScope(string permission)
    {
        if (string.IsNullOrEmpty(permission))
            return PermissionScopeKind.Instance;

        var separator = permission.IndexOf('.');

        if (separator <= 0)
            return PermissionScopeKind.Instance;

        return _projectGroups.Contains(permission[..separator]) ? PermissionScopeKind.Project : PermissionScopeKind.Instance;
    }

    /// <summary>
    /// True when <paramref name="permission"/> applies inside a project.
    /// </summary>
    public static bool IsProjectPermission(string permission) => GetScope(permission) == PermissionScopeKind.Project;
}
