using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.DataAccess.EfCore.Utils.LookupModels;

namespace Milvaion.Application.Interfaces;

/// <summary>
/// Lookup service for getting dynamic entity fetch.
/// </summary>
public interface ILookupService : IInterceptable
{
    /// <summary>
    /// Dynamic entity fetch.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    Task<List<object>> GetLookupsAsync(LookupRequest request);

    /// <summary>
    /// Get enum names as localized.
    /// </summary>
    /// <param name="enumName"></param>
    /// <returns></returns>
    ListResponse<EnumLookupModel> GetEnumLookups(string enumName);
}
