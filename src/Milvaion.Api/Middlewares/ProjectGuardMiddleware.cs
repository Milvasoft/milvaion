using Microsoft.AspNetCore.Authentication;
using Milvaion.Api.Services.Projects;
using Milvaion.Api.Utils;
using Milvaion.Application.Utils.Projects;

namespace Milvaion.Api.Middlewares;

/// <summary>
/// Runs between authentication and authorization. Makes sure every request has a project resolution in
/// <c>HttpContext.Items</c> (the Default project for anonymous requests), answers requests whose project cannot be
/// used, and blocks writes to archived projects.
/// </summary>
public class ProjectGuardMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        // Api keys are not the default scheme, so the authorization policy would authenticate them only after this
        // point. Authenticate here, which runs the claims transformation and records the key's project.
        if (!context.Items.ContainsKey(ProjectResolution.ItemKey) && context.Request.Headers.ContainsKey(MessageConstant.ApiKey))
            await context.AuthenticateAsync(ApiKeyAuthenticationDefaults.AuthenticationScheme);

        if (context.Items[ProjectResolution.ItemKey] is not ProjectResolution resolution)
        {
            context.Items[ProjectResolution.ItemKey] = ProjectRequestResolver.DefaultResolution;

            await _next(context);
            return;
        }

        if (!resolution.Succeeded)
        {
            await WriteErrorAsync(context, resolution.Error!.Value);
            return;
        }

        if (resolution.IsArchived && !(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || HttpMethods.IsOptions(context.Request.Method)))
        {
            await WriteErrorAsync(context, ProjectResolutionError.ProjectArchived);
            return;
        }

        await _next(context);
    }

    private static Task WriteErrorAsync(HttpContext context, ProjectResolutionError error)
    {
        var (messageKey, statusCode) = error switch
        {
            ProjectResolutionError.ProjectNotFound => (MessageKey.ProjectNotFound, StatusCodes.Status404NotFound),
            ProjectResolutionError.NotMember => (MessageKey.ProjectNotMember, StatusCodes.Status403Forbidden),
            ProjectResolutionError.ApiKeyProjectMismatch => (MessageKey.ProjectApiKeyMismatch, StatusCodes.Status403Forbidden),
            ProjectResolutionError.ProjectRequired => (MessageKey.ProjectRequired, StatusCodes.Status400BadRequest),
            ProjectResolutionError.ProjectArchived => (MessageKey.ProjectArchived, StatusCodes.Status403Forbidden),
            _ => (MessageKey.Forbidden, StatusCodes.Status403Forbidden)
        };

        return ExceptionMiddleware.RewriteResponseAsync(context, messageKey, statusCode, MessageType.Warning);
    }
}
