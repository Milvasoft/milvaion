using Microsoft.Extensions.DependencyInjection;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Components.Rest.OptionsDataFetcher.EnumValueFetcher;
using System.Reflection;

namespace Milvaion.Application.Utils.LinkedWithFormatters;

/// <summary>
/// Formatter service collection extensions.
/// </summary>
public static class FormatterServiceCollectionExtensions
{
    /// <summary>
    /// Add linked with formatters to service collection.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="assemblies"></param>
    /// <returns></returns>
    public static IServiceCollection AddLinkedWithFormatters(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddEnumFormatters(assemblies);
        services.AddKeyedScoped<ILinkedWithFormatter, ByteArrayToBase64Formatter>(ByteArrayToBase64Formatter.FormatterName);
        services.AddKeyedScoped<ILinkedWithFormatter, YesNoFormatter>(YesNoFormatter.FormatterName);
        services.AddKeyedScoped<ILinkedWithFormatter, ExistsNotFormatter>(ExistsNotFormatter.FormatterName);
        services.AddKeyedScoped<ILinkedWithFormatter, PageNameTranslateFormatter>(PageNameTranslateFormatter.FormatterName);
        services.AddKeyedScoped<ILinkedWithFormatter, LanguageIdNameFormatter>(LanguageIdNameFormatter.FormatterName);

        return services;
    }

    /// <summary>
    /// Add linked with formatters to service collection.
    /// </summary>
    /// <param name="services"></param>
    /// <returns></returns>
    public static IServiceCollection AddFetchers(this IServiceCollection services)
    {
        services.AddKeyedScoped<IOptionsDataFetcher, EnumLocalizedValueFetcher>(EnumLocalizedValueFetcher.FetcherName);
        services.AddKeyedScoped<IOptionsDataFetcher, BoolLocalizedValueFetcher>(BoolLocalizedValueFetcher.FetcherName);
        services.AddKeyedScoped<IOptionsDataFetcher, LookupFetcher>(LookupFetcher.FetcherName);

        return services;
    }

    /// <summary>
    /// Finds enums in assemblies and adds their enum formatters to the service collection.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="assemblies"></param>
    /// <returns></returns>
    private static IServiceCollection AddEnumFormatters(this IServiceCollection services, params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            var types = assembly.GetExportedTypes()?.Where(t => t.IsEnum);

            if (types != null)
                foreach (var type in types)
                    services.AddKeyedScoped(typeof(ILinkedWithFormatter), $"Enum.{type.Name}", typeof(EnumFormatter<>).MakeGenericType(type));
        }

        return services;
    }
}
