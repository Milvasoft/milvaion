using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvaion.Domain.JsonModels;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Core.Exceptions;
using Milvasoft.Core.Helpers;
using Milvasoft.Interception.Interceptors.ActivityScope;
using Npgsql;
using System.Net;

namespace Milvaion.Api.Middlewares;

/// <summary>
/// Catches errors occurring elsewhere in the project.
/// </summary>
/// <remarks>
/// Constructor of <see cref="ExceptionMiddleware"/> class.
/// </remarks>
/// <param name="next"></param>
/// <param name="loggerFactory"></param>
public class ExceptionMiddleware(RequestDelegate next, ILoggerFactory loggerFactory)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger _logger = loggerFactory.CreateLogger<ExceptionMiddleware>();

    /// <summary>
    /// Invokes the method or constructor reflected by this MethodInfo instance.
    /// </summary>
    /// <param name="context"></param>
    /// <returns></returns>
    public async Task Invoke(HttpContext context)
    {
        try
        {
            //Continue processing
            if (_next != null && !context.Response.HasStarted)
                await _next.Invoke(context);
        }
        catch (PostgresException ex)
        {
            context.Items.Add(nameof(Exception), ex);

            await HandlePostgresException(context, ex);
        }
        catch (MilvaUserFriendlyException ex)
        {
            context.Items.Add(nameof(Exception), ex);

            if (context.Response.StatusCode == StatusCodes.Status401Unauthorized || context.Response.StatusCode == StatusCodes.Status403Forbidden)
            {
                var messageKey = ex.Message?.StartsWith('~') ?? false
                    ? ex.Message.Trim('~')
                    : (context.Response.StatusCode == StatusCodes.Status401Unauthorized ? MessageKey.Unauthorized : MessageKey.Forbidden);

                await RewriteResponseAsync(context, messageKey, context.Response.StatusCode, MessageType.Warning);
                return;
            }
            else if (context.Response.StatusCode == StatusCodes.Status419AuthenticationTimeout)
            {
                await RewriteResponseAsync(context, MessageKey.SessionTimeout, context.Response.StatusCode, MessageType.Warning);
                return;
            }

            await RewriteResponseAsync(context, ex.Message, StatusCodes.Status200OK, MessageType.Warning, ex.ExceptionObject);
        }
        catch (Exception ex)
        {
            context.Items.Add(nameof(Exception), ex);

            if (ex.InnerException is PostgresException postgresEx)
                await HandlePostgresException(context, postgresEx);
            else
            {
                LogExceptionWithRequest(context, ex);

                await RewriteResponseAsync(context, MessageKey.UnhandledException, (int)HttpStatusCode.BadRequest);
            }
        }
    }

    private Task HandlePostgresException(HttpContext context, PostgresException ex)
    {
        string messageKey;

        if (ex.SqlState.Equals(MessageConstant.DefaultDataCannotModifyPgCode) && ex.Message.Contains(MessageConstant.DefaultDataCannotModifyPgMessage))
            messageKey = MessageKey.DefaultValueCannotModify;
        else if (ex.SqlState.Equals(MessageConstant.DuplicateDataViolationPgCode))
            messageKey = MessageKey.PostgreDuplicateDataException;
        else if (ex.SqlState.Equals(MessageConstant.ForeignKeyViolationPgCode))
            messageKey = MessageKey.PostgreBasedException;
        else
        {
            LogExceptionWithRequest(context, ex);

            messageKey = MessageKey.PostgreBasedException;
        }

        return RewriteResponseAsync(context, messageKey, StatusCodes.Status200OK, MessageType.Warning);
    }

    /// <summary>
    /// Rewrites the response.
    /// </summary>
    /// <param name="context"></param>
    /// <param name="messageKey"></param>
    /// <param name="statusCode"></param>
    /// <param name="messageType"></param>
    /// <param name="exceptionObject"></param>
    /// <returns></returns>
    public static async Task RewriteResponseAsync(HttpContext context, string messageKey, int statusCode, MessageType messageType = MessageType.Error, object exceptionObject = null)
    {
        if (!context.Response.HasStarted)
        {
            var localizer = context.RequestServices.GetService<IMilvaLocalizer>();

            var message = exceptionObject is null ? localizer[messageKey] : localizer[messageKey, exceptionObject];

            var response = Response.Error(message, messageType);

            response.StatusCode = statusCode;
            context.Response.StatusCode = statusCode;

            await context.Response.WriteAsJsonAsync(response);
        }
    }

    private void LogExceptionWithRequest(HttpContext context, Exception ex)
    {
        if (!_logger.IsEnabled(LogLevel.Error))
            return;

        var requestInfo = new RequestInfo
        {
            Method = context.Request.Method,
            Headers = context.Request.Headers,
            ContentLength = context.Request.ContentLength ?? 0,
            AbsoluteUri = string.Concat(context.Request.Scheme,
                                    GlobalConstant.UrlStartSegment,
                                    context.Request.Host.ToUriComponent(),
                                    context.Request.PathBase.ToUriComponent(),
                                    context.Request.Path.ToUriComponent(),
                                    context.Request.QueryString.ToUriComponent()),
            QueryString = context.Request.QueryString.ToUriComponent()
        };

        _logger.LogError(LogTemplate.ExceptionWithRequest,
                         ex.Message,
                         ActivityHelper.Id,
                         LogLevel.Information.ToString(),
                         DateTimeOffset.UtcNow,
                         context.Request.Method,
                         context.Request.Path,
                         requestInfo,
                         User.GetCurrentUser(context.RequestServices.GetService<IServiceProvider>()),
                         ex.ToJson());
    }
}
