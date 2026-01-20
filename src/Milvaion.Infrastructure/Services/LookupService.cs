using Milvaion.Application.Dtos;
using Milvaion.Application.Interfaces;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions.Localization;
using Milvasoft.DataAccess.EfCore.Utils.LookupModels;
using Milvasoft.Interception.Interceptors.Logging;
using System.Reflection;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Lookup service for getting dynamic entity fetch.
/// </summary>
/// <param name="dbContext"></param>
/// <param name="localizer"></param>
public class LookupService(MilvaionDbContext dbContext, IMilvaLocalizer localizer) : ILookupService
{
    private readonly MilvaionDbContext _dbContext = dbContext;
    private readonly IMilvaLocalizer _localizer = localizer;

    /// <summary>
    /// Dynamic entity fetch.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    public async Task<List<object>> GetLookupsAsync(LookupRequest request)
    {
        var response = await _dbContext.GetLookupsAsync(request);

        return response;
    }

    /// <summary>
    /// Get enum names as localized.
    /// </summary>
    /// <param name="enumName"></param>
    /// <returns></returns>
    [Log]
    public ListResponse<EnumLookupModel> GetEnumLookups(string enumName)
    {
        if (string.IsNullOrWhiteSpace(enumName))
            return ListResponse<EnumLookupModel>.Error();

        var enumType = GetEnumType(enumName);

        if (enumType == null)
            return ListResponse<EnumLookupModel>.Error();

        var enumValues = Enum.GetValues(enumType);

        var enumUnderlyingType = Enum.GetUnderlyingType(enumType);

        var enumLookups = new List<EnumLookupModel>();

        foreach (var enumValue in enumValues)
        {
            var resourceKey = $"{enumType.Name}.{enumValue}";

            var localizedEnumValue = _localizer[resourceKey];

            string localizedValue;

            if (localizedEnumValue?.ResourceFound ?? false)
                localizedValue = localizedEnumValue.ToString();
            else
                localizedValue = enumValue.ToString();

            var enumActualValue = Convert.ChangeType(enumValue, enumUnderlyingType);

            enumLookups.Add(new EnumLookupModel { Value = enumActualValue, Name = localizedValue });
        }

        return ListResponse<EnumLookupModel>.Success(enumLookups);
    }

    private static Type GetEnumType(string enumName)
    {
        var assemblies = new List<Assembly> { DomainAssembly.Assembly, InfrastructureAssembly.Assembly, DomainAssembly.Assembly };

        foreach (var assembly in assemblies)
        {
            var type = Array.Find(assembly.GetExportedTypes(), t => t.IsEnum && t.Name == enumName);

            if (type == null)
                continue;

            if (type.IsEnum)
                return type;
        }

        return null;
    }
}
