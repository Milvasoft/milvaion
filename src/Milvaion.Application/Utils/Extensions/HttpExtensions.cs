using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Milvasoft.Core.Exceptions;
using System.Net;
using System.Net.Sockets;
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
        if (httpContext.User.Identity?.IsAuthenticated != true)
            return [];

        // Authentication has already validated the credential, whichever scheme it came through.
        return httpContext.User.Claims;
    }

    /// <summary>
    /// Throws an exception if the current user does not have the required permissions.
    /// </summary>
    /// <param name="httpContext"></param>
    /// <param name="permissions"></param>
    /// <returns></returns>
    public static void ThrowIfCurrentUserNotAuthorized(this HttpContext httpContext, params List<string> permissions)
    {
        var hasPermission = httpContext.GetCurrentUserPermissions().Any(permission => permission == PermissionCatalog.App.SuperAdmin || permissions.Contains(permission));

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
        string requestIp;

        // The header is only as trustworthy as whoever set it, so it counts only when the connection comes from a
        // proxy on the local network. Anyone connecting directly could otherwise claim any address.
        if (IsLocalNetworkAddress(httpContext.Connection.RemoteIpAddress)
            && httpContext.Request.Headers.TryGetValue(GlobalConstant.RealIpHeaderKey, out StringValues realIpAddress)
            && IPAddress.TryParse(realIpAddress.ToString(), out _))
            requestIp = realIpAddress.ToString();
        else
        {
            requestIp = httpContext.Connection.RemoteIpAddress?.MapToIPv4().ToString();

            if (requestIp == GlobalConstant.DefaultIp)
                requestIp = httpContext.Connection.LocalIpAddress?.MapToIPv4().ToString();
        }

        return requestIp;
    }

    private static bool IsLocalNetworkAddress(IPAddress address)
    {
        // In-process hosts have no remote address, and nothing can reach them over the network.
        if (address is null || IPAddress.IsLoopback(address))
            return true;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;

        var bytes = address.GetAddressBytes();

        return bytes[0] == 10
               || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
               || (bytes[0] == 192 && bytes[1] == 168)
               || (bytes[0] == 169 && bytes[1] == 254);
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
