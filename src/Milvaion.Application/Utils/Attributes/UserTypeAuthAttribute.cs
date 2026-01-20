using Microsoft.AspNetCore.Mvc.Filters;

namespace Milvaion.Application.Utils.Attributes;

/// <summary>
/// Determines the type of user who can access the method.
/// </summary>
/// <param name="userType"></param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class UserTypeAuthAttribute(UserType userType) : Attribute, IAuthorizationFilter
{
    private readonly UserType _userType = userType;

    /// <inheritdoc/>
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var currentUserType = context.HttpContext.GetCurrentUserType();

        if (currentUserType == null || (_userType & currentUserType) != currentUserType)
        {
            context.HttpContext.Response.ThrowWithForbidden();
        }
    }
}