using Microsoft.AspNetCore.Authorization;

namespace Milvaion.Application.Utils.Attributes;

/// <summary>
/// Specifies that the method marked with this attribute will be added to as activity to database by the <see cref="UserActivityLogInterceptor"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AuthAttribute : AuthorizeAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AuthAttribute"/> class with <see cref="PermissionCatalog.App.SuperAdmin"/> role.
    /// </summary>
    public AuthAttribute() : base() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="AuthAttribute"/> class with <see cref="PermissionCatalog.App.SuperAdmin"/> role and <paramref name="roles"/>.
    /// </summary>
    public AuthAttribute(params string[] roles) : base()
    {
        if (string.IsNullOrWhiteSpace(Roles))
            Roles = PermissionCatalog.App.SuperAdmin;
        else
            Roles = string.Join(",", Roles, PermissionCatalog.App.SuperAdmin);

        var joinedRoles = string.Join(",", roles);

        Roles = string.Join(",", Roles, joinedRoles);
    }
}
