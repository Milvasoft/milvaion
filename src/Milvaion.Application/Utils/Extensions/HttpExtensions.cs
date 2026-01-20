using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Milvasoft.Core.Exceptions;
using Milvasoft.Identity.Abstract;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Milvaion.Application.Utils.Extensions;

/// <summary>
/// Contains extension methods for the Milvaion.
/// </summary>
public static partial class MilvaionExtensions
{
    /// <summary>
    /// Gets token from header via HttpContextAccessor.
    /// </summary>
    /// <param name="httpContextAccessor"></param>
    /// <returns></returns>
    public static string GetTokenFromHeader(this IHttpContextAccessor httpContextAccessor)
         => httpContextAccessor.HttpContext.GetTokenFromHeader();

    /// <summary>
    /// Gets token from header via HttpContext.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static string GetTokenFromHeader(this HttpContext httpContext)
         => httpContext.Request.Headers.Authorization.FirstOrDefault()?.Split(" ").LastOrDefault();

    /// <summary>
    /// Checks whether the current user is the user whose name is given as a parameter via HttpContext.
    /// </summary>
    /// <param name="httpContextAccessor"></param>
    /// <param name="userName"></param>
    /// <returns></returns>
    public static bool IsCurrentUser(this IHttpContextAccessor httpContextAccessor, string userName)
        => httpContextAccessor.HttpContext.User.Identity.Name == userName;

    /// <summary>
    /// Gets current user name via HttpContextAccessor.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static string CurrentUserName(this HttpContext httpContext) => httpContext.User?.Identity?.Name;

    /// <summary>
    /// Get current user type from claims.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static UserType? GetCurrentUserType(this HttpContext httpContext)
    {
        var claims = httpContext.GetCurrentUserClaims();

        var userType = claims.FirstOrDefault(p => p.Type == GlobalConstant.UserTypeClaimName);

        return userType != null ? Enum.Parse<UserType>(userType.Value) : null;
    }

    /// <summary>
    /// Get current user permissions from claims.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static IEnumerable<string> GetCurrentUserPermissions(this HttpContext httpContext)
    {
        var claims = httpContext.GetCurrentUserClaims();

        var permissions = claims.Where(p => p.Type == ClaimTypes.Role).Select(i => i.Value);

        return permissions;
    }

    /// <summary>
    /// Checks whether the current user is the user whose name is given as a parameter via HttpContext.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static IEnumerable<Claim> GetCurrentUserClaims(this HttpContext httpContext)
    {
        if (!httpContext.User.Identity.IsAuthenticated)
            return [];

        var token = httpContext.GetTokenFromHeader();

        var tokenManager = httpContext.RequestServices.GetService<IMilvaTokenManager>();

        var claimsPrincipal = tokenManager.GetClaimsPrincipalIfValid(token);

        return claimsPrincipal?.Claims ?? [];
    }

    /// <summary>
    /// Throws an exception if the current user does not have the required permissions.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <param name="permissions"></param>
    /// <returns></returns>
    public static void ThrowIfCurrentUserNotAuthorized(this HttpContext httpContext, params List<string> permissions)
    {
        var currentUserPermissonExists = httpContext.Items.TryGetValue(GlobalConstant.CurrentUserPermissionsKey, out var contextItem);

        if (!currentUserPermissonExists)
            httpContext.Response.ThrowWithForbidden();

        var currentUserPermissons = (IEnumerable<string>)contextItem;

        permissions.Add(PermissionCatalog.App.SuperAdmin);

        var hasPermission = currentUserPermissons.Any(permissions.Contains);

        if (!hasPermission)
            httpContext.Response.ThrowWithForbidden();
    }

    /// <summary>
    /// Gets the IP address of the client.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <returns></returns>
    public static string GetIpAddress(this HttpContext httpContext)
    {
        var realIpExists = httpContext.Request.Headers.TryGetValue(GlobalConstant.RealIpHeaderKey, out StringValues realIpAddress);

        string requestIp;

        if (realIpExists)
            requestIp = realIpAddress.ToString();
        else
        {
            requestIp = httpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();

            if (requestIp == GlobalConstant.DefaultIp)
                requestIp = httpContext.Connection.LocalIpAddress?.MapToIPv4().ToString();
        }

        return requestIp;
    }

    /// <summary>
    /// Sets the status code of the response to 401.
    /// </summary>
    /// <param name="response"></param>
    /// <param name="messageKey"></param>
    public static void ThrowWithUnauthorized(this HttpResponse response, string messageKey = null)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        throw new MilvaUserFriendlyException(messageKey is not null ? "~" + messageKey : messageKey);
    }

    /// <summary>
    /// Sets the status code of the response to 403.
    /// </summary>
    /// <param name="response"></param>
    /// <param name="messageKey"></param>
    public static void ThrowWithForbidden(this HttpResponse response, string messageKey = null)
    {
        response.StatusCode = StatusCodes.Status403Forbidden;
        throw new MilvaUserFriendlyException(messageKey is not null ? "~" + messageKey : messageKey);
    }

    /// <summary>
    /// Sets the status code of the response to 419.
    /// </summary>
    /// <param name="response"></param>
    public static void ThrowWithSessionTimeout(this HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status419AuthenticationTimeout;
        throw new MilvaUserFriendlyException();
    }

    /// <summary>
    /// Makes reques url from requested params.For more detailed information of URL Sections, please refer to the remarks section where the method is described.
    /// </summary>
    /// <param name="protocol"></param>
    /// <param name="hostName"></param>
    /// <param name="port"></param>
    /// <param name="pathName"></param>
    /// <param name="query"></param>
    /// <param name="hash"></param>
    /// <returns></returns>
    public static string CreateRequestUrl(string protocol, string hostName, string port, string pathName, string query = "", string hash = "")
    {
        query = query == "" ? query : "?" + query;
        hash = hash == "" ? hash : "#" + hash;
        var text = $"{protocol}://{hostName}:{port}/{pathName}{query}{hash}";

        Match match = UrlRegex().Match(text);

        if (!match.Success)
            throw new MilvaDeveloperException("Invalid url");

        return text;
    }
}
