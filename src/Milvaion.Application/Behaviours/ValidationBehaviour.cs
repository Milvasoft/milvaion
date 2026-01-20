using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.Core.Exceptions;
using Milvasoft.Core.Helpers;
using Milvasoft.Interception.Interceptors.Response;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Net;

namespace Milvaion.Application.Behaviours;

/// <summary>
/// Validation behavior for return value is <see cref="Response"/> typed requests.
/// </summary>
/// <typeparam name="TRequest"></typeparam>
/// <typeparam name="TResponse"></typeparam>
/// <param name="validators"></param>
/// <param name="serviceProvider"></param>
public sealed class ValidationBehaviorForResponse<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators,
                                                                       IServiceProvider serviceProvider) : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : class
{
    private readonly IEnumerable<IValidator<TRequest>> _validators = validators;
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private static readonly ConcurrentDictionary<Type, Func<object>> _typeFactoryCache = new();

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var context = new ValidationContext<TRequest>(request);

        var validationFailures = await Task.WhenAll(_validators.Select(validator => validator.ValidateAsync(context)));

        var errors = validationFailures.Where(validationResult => !validationResult.IsValid)
                                       .SelectMany(validationResult => validationResult.Errors)
                                       .Select(e => e.ErrorMessage);

        if (!errors.IsNullOrEmpty())
            return CreateFailureResponse(errors);

        var response = await next(cancellationToken);

        return response;
    }

    private TResponse CreateFailureResponse(IEnumerable<string> errors)
    {
        if (typeof(TResponse) == typeof(Response))
        {
            var response = new Response();

            AddMessagesToResponse(response, errors);

            return response as TResponse;
        }
        else if (typeof(TResponse).IsGenericType && typeof(TResponse).GetGenericTypeDefinition() == typeof(Response<>))
        {
            return BuildGenericResponse(typeof(Response<>), errors);
        }
        else if (typeof(TResponse).IsGenericType && typeof(TResponse).GetGenericTypeDefinition() == typeof(ListResponse<>))
        {
            return BuildGenericResponse(typeof(ListResponse<>), errors);
        }

        var localizer = _serviceProvider.GetService<IMilvaLocalizer>();

        throw new MilvaUserFriendlyException(localizer[errors.FirstOrDefault()]);
    }

    private TResponse BuildGenericResponse(Type type, IEnumerable<string> errors)
    {
        var responseType = typeof(TResponse).GetGenericArguments()[0];

        var response = CreateInstance(type.MakeGenericType(responseType));

        if (response is Response r)
        {
            AddMessagesToResponse(r, errors);

            var interceptionOptions = _serviceProvider.GetService<IResponseInterceptionOptions>();

            var metadataGenerator = new ResponseMetadataGenerator(_serviceProvider);

            metadataGenerator.GenerateMetadata(response as IHasMetadata);

            if (interceptionOptions.TranslateResultMessages && !r.Messages.IsNullOrEmpty())
            {
                var localizer = _serviceProvider.GetService<IMilvaLocalizer>();

                if (localizer != null)
                    foreach (var message in r.Messages)
                        message.Message = localizer[message.Message];
            }
        }

        return (TResponse)response;
    }

    private static void AddMessagesToResponse(Response response, IEnumerable<string> errors)
    {
        response.StatusCode = (int)HttpStatusCode.BadRequest;
        response.IsSuccess = false;

        foreach (var error in errors.Distinct())
            response.AddMessage(error, MessageType.Validation);
    }

    private static object CreateInstance(Type type) => _typeFactoryCache.GetOrAdd(type, t =>
    {
        var ctor = Expression.New(t);

        var lambda = Expression.Lambda<Func<object>>(ctor);
        return lambda.Compile();
    })();
}
