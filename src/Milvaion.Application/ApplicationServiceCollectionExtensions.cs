using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Behaviours;
using System.Reflection;

namespace Milvaion.Application;

/// <summary>
/// Infrastructure service collection extensions.
/// </summary>
public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Adds infrastructure services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="assemblies"></param>
    /// <returns></returns>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services, Assembly[] assemblies)
    {
        services.AddMediator(assemblies);

        return services;
    }

    /// <summary>
    /// Adds mediatR services.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="assemblies"></param>
    /// <returns></returns>
    private static IServiceCollection AddMediator(this IServiceCollection services, Assembly[] assemblies)
    {
        services.AddValidatorsFromAssembly(ApplicationAssembly.Assembly);

        services.AddMediatR(opt =>
        {
            opt.RegisterServicesFromAssemblies(assemblies);
            opt.AddOpenBehavior(typeof(ValidationBehaviorForResponse<,>));
        });

        return services;
    }
}
