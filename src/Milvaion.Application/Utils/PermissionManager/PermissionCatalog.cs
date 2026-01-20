using Milvasoft.Core.Helpers;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;

namespace Milvaion.Application.Utils.PermissionManager;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public static partial class PermissionCatalog
{
    public static ConcurrentBag<Permission> Permissions { get; internal set; } = [];

    [Description("Application-wide permissions")]
    public static class App
    {
        [Description("Provides access to the entire system.")]
        public const string SuperAdmin = "App.SuperAdmin";
    }

    [Description("User Management")]
    public static class UserManagement
    {
        [Description("User list permission.")]
        public const string List = "UserManagement.List";

        [Description("User detail view permission")]
        public const string Detail = "UserManagement.Detail";

        [Description("User create permission")]
        public const string Create = "UserManagement.Create";

        [Description("User update permission")]
        public const string Update = "UserManagement.Update";

        [Description("User delete permission")]
        public const string Delete = "UserManagement.Delete";
    }

    [Description("Role Management")]
    public static class RoleManagement
    {
        [Description("Role list permission.")]
        public const string List = "RoleManagement.List";

        [Description("Role detail view permission")]
        public const string Detail = "RoleManagement.Detail";

        [Description("Role create permission")]
        public const string Create = "RoleManagement.Create";

        [Description("Role update permission")]
        public const string Update = "RoleManagement.Update";

        [Description("Role delete permission")]
        public const string Delete = "RoleManagement.Delete";
    }

    [Description("Content Management")]
    public static class ContentManagement
    {
        [Description("Content list permission.")]
        public const string List = "ContentManagement.List";

        [Description("Content detail view permission")]
        public const string Detail = "ContentManagement.Detail";

        [Description("Content create permission")]
        public const string Create = "ContentManagement.Create";

        [Description("Content update permission")]
        public const string Update = "ContentManagement.Update";

        [Description("Content delete permission")]
        public const string Delete = "ContentManagement.Delete";
    }

    [Description("Content Namespace Management")]
    public static class NamespaceManagement
    {
        [Description("Content Namespace list permission.")]
        public const string List = "NamespaceManagement.List";

        [Description("Content Namespace detail view permission")]
        public const string Detail = "NamespaceManagement.Detail";

        [Description("Content Namespace create permission")]
        public const string Create = "NamespaceManagement.Create";

        [Description("Content Namespace update permission")]
        public const string Update = "NamespaceManagement.Update";

        [Description("Content Namespace delete permission")]
        public const string Delete = "NamespaceManagement.Delete";
    }

    [Description("Content Resource Group Management")]
    public static class ResourceGroupManagement
    {
        [Description("Content Resource Group list permission.")]
        public const string List = "ResourceGroupManagement.List";

        [Description("Content Resource Group detail view permission")]
        public const string Detail = "ResourceGroupManagement.Detail";

        [Description("Content Resource Group create permission")]
        public const string Create = "ResourceGroupManagement.Create";

        [Description("Content Resource Group update permission")]
        public const string Update = "ResourceGroupManagement.Update";

        [Description("Content Resource Group delete permission")]
        public const string Delete = "ResourceGroupManagement.Delete";
    }

    [Description("Permission Management")]
    public static class PermissionManagement
    {
        [Description("Permission list permission.")]
        public const string List = "PermissionManagement.List";
    }

    [Description("Activity Log Management")]
    public static class ActivityLogManagement
    {
        [Description("Activity Log list permission.")]
        public const string List = "ActivityLogManagement.List";
    }

    [Description("Language Management")]
    public static class LanguageManagement
    {
        [Description("Language list permission.")]
        public const string List = "LanguageManagement.List";

        [Description("Language update permission.")]
        public const string Update = "LanguageManagement.Update";
    }

    [Description("Internal Notification Management")]
    public static class InternalNotificationManagement
    {
        [Description("Internal Notification list permission.")]
        public const string List = "InternalNotificationManagement.List";

        [Description("Internal Notification detail view permission")]
        public const string Detail = "InternalNotificationManagement.Detail";

        [Description("Internal Notification create permission")]
        public const string Create = "InternalNotificationManagement.Create";

        [Description("Internal Notification update permission")]
        public const string Update = "InternalNotificationManagement.Update";

        [Description("Internal Notification delete permission")]
        public const string Delete = "InternalNotificationManagement.Delete";
    }

    [Description("Scheduled Job Management")]
    public static class ScheduledJobManagement
    {
        [Description("Scheduled Job list permission.")]
        public const string List = "ScheduledJobManagement.List";

        [Description("Scheduled Job detail view permission")]
        public const string Detail = "ScheduledJobManagement.Detail";

        [Description("Scheduled Job create permission")]
        public const string Create = "ScheduledJobManagement.Create";

        [Description("Scheduled Job update permission")]
        public const string Update = "ScheduledJobManagement.Update";

        [Description("Scheduled Job delete permission")]
        public const string Delete = "ScheduledJobManagement.Delete";

        [Description("Scheduled Job cancel permission")]
        public const string Cancel = "ScheduledJobManagement.Cancel";

        [Description("Scheduled Job trigger permission")]
        public const string Trigger = "ScheduledJobManagement.Trigger";
    }

    [Description("Worker Management")]
    public static class WorkerManagement
    {
        [Description("Worker list permission.")]
        public const string List = "WorkerManagement.List";

        [Description("Worker detail view permission")]
        public const string Detail = "WorkerManagement.Detail";

        [Description("Worker delete permission")]
        public const string Delete = "WorkerManagement.Delete";
    }

    [Description("System Administration Management")]
    public static class SystemAdministration
    {
        [Description("System Administration list permission.")]
        public const string List = "SystemAdministration.List";

        [Description("System Administration detail view permission")]
        public const string Detail = "SystemAdministration.Detail";

        [Description("System Administration update permission")]
        public const string Update = "SystemAdministration.Update";

        [Description("System Administration delete permission")]
        public const string Delete = "SystemAdministration.Delete";
    }

    [Description("Failed Job Management")]
    public static class FailedOccurrenceManagement
    {
        [Description("Failed Job list permission.")]
        public const string List = "FailedOccurrenceManagement.List";

        [Description("Failed Job detail view permission")]
        public const string Detail = "FailedOccurrenceManagement.Detail";

        [Description("Failed Job create permission")]
        public const string Create = "FailedOccurrenceManagement.Create";

        [Description("Failed Job update permission")]
        public const string Update = "FailedOccurrenceManagement.Update";

        [Description("Failed Job delete permission")]
        public const string Delete = "FailedOccurrenceManagement.Delete";
    }

    /// <summary>
    /// Gets all permissions in the system as grouped by permission group.
    /// </summary>
    /// <returns> Permission group and group's permissions pair. </returns>
    public static Dictionary<string, List<Permission>> GetPermissionsAndGroups()
    {
        var permissionGroups = new Dictionary<string, List<Permission>>();

        var permissionTypes = typeof(PermissionCatalog).GetNestedTypes().Where(t => !t.IsValueType);

        foreach (var permissionType in permissionTypes)
        {
            var permissions = PermissionBase.GetPermissions(permissionType).ToList();

            permissionGroups.Add(permissionType.Name, permissions);
        }

        return permissionGroups;
    }
}

public static class PermissionBase
{
    public static IEnumerable<Permission> Add<T>() => GetPermissions(typeof(T));

    public static IEnumerable<Permission> GetPermissions(Type type)
    {
        foreach (var field in type.GetFields())
        {
            var permission = new Permission
            {
                Name = field.Name,
                NormalizedName = field.Name.MilvaNormalize(),
                Description = field.GetCustomAttribute<DescriptionAttribute>()?.Description,
                PermissionGroup = type.Name,
                PermissionGroupDescription = type.GetCustomAttribute<DescriptionAttribute>()?.Description
            };

            yield return permission;
        }
    }
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member