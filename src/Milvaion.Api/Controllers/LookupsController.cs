using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.DataAccess.EfCore.Utils.LookupModels;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Endpoints for lookup.
/// </summary>
/// <param name="lookupService"></param>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager | UserType.AppUser)]
public class LookupsController(ILookupService lookupService) : ControllerBase
{
    private readonly ILookupService _lookupService = lookupService;

    /// <summary>
    /// It is a dynamic endpoint that client applications can use to fetch data into comboboxes. Please review the documentation for usage information.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    [HttpPatch]
    [Auth]
    public async Task<ListResponse<object>> GetLookupsAsync(LookupRequest request)
    {
        var lookups = await _lookupService.GetLookupsAsync(request);

        return ListResponse<object>.Success(lookups);
    }

    /// <summary>
    /// Get enum lookups.
    /// </summary>
    /// <param name="enumName"></param>
    /// <returns></returns>
    [HttpGet("enum")]
    public ListResponse<EnumLookupModel> GetEnumLookups(string enumName)
    {
        var response = _lookupService.GetEnumLookups(enumName);

        return response;
    }
}
