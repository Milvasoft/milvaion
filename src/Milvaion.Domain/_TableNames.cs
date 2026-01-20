namespace Milvaion.Domain;

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
public static class TableNames
{
    public const string MigrationHistory = "_MigrationHistory";
    public const string EfMigrationHistory = "_EFMigrationHistory";
    public const string Users = nameof(Users);
    public const string Roles = nameof(Roles);
    public const string UserRoleRelations = nameof(UserRoleRelations);
    public const string Permissions = nameof(Permissions);
    public const string RolePermissionRelations = nameof(RolePermissionRelations);
    public const string ActivityLogs = nameof(ActivityLogs);
    public const string ApiLogs = nameof(ApiLogs);
    public const string MethodLogs = nameof(MethodLogs);
    public const string UserSessions = nameof(UserSessions);
    public const string UserSessionHistories = nameof(UserSessionHistories);
    public const string MenuItems = nameof(MenuItems);
    public const string MenuItemTranslations = nameof(MenuItemTranslations);
    public const string MenuGroups = nameof(MenuGroups);
    public const string MenuGroupTranslations = nameof(MenuGroupTranslations);
    public const string Pages = nameof(Pages);
    public const string PageActions = nameof(PageActions);
    public const string PageActionTranslations = nameof(PageActionTranslations);
    public const string Contents = nameof(Contents);
    public const string Medias = nameof(Medias);
    public const string Namespaces = nameof(Namespaces);
    public const string ResourceGroups = nameof(ResourceGroups);
    public const string Languages = nameof(Languages);
    public const string InternalNotifications = nameof(InternalNotifications);
    public const string ScheduledJobs = nameof(ScheduledJobs);
    public const string JobOccurrences = nameof(JobOccurrences);
}
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member