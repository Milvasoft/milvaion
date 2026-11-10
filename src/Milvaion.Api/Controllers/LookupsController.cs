using Asp.Versioning;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Extensions;
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
[Auth]
public class LookupsController(ILookupService lookupService) : ControllerBase
{
    private readonly ILookupService _lookupService = lookupService;

    // The dynamic query reaches these entities whatever the caller's role, so the list permission of each has to be enforced here.
    private static readonly Dictionary<string, string> _entityPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["User"] = PermissionCatalog.UserManagement.List,
        ["Role"] = PermissionCatalog.RoleManagement.List,
        ["ScheduledJob"] = PermissionCatalog.ScheduledJobManagement.List,
        ["JobOccurrence"] = PermissionCatalog.ScheduledJobManagement.List,
    };

    private static readonly string[] _sensitivePropertyFragments = ["password", "securitystamp", "secret", "token", "hash"];

    /// <summary>
    /// It is a dynamic endpoint that client applications can use to fetch data into comboboxes. Please review the documentation for usage information.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    [HttpPatch]
    [Auth]
    public async Task<ListResponse<object>> GetLookupsAsync(LookupRequest request)
    {
        EnsureLookupAllowed(request);

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

    private void EnsureLookupAllowed(LookupRequest request)
    {
        foreach (var parameter in request?.Parameters ?? [])
        {
            var permissionMissing = parameter.EntityName is not null
                                    && _entityPermissions.TryGetValue(parameter.EntityName, out var permission)
                                    && !User.IsInRole(permission)
                                    && !User.IsInRole(PermissionCatalog.App.SuperAdmin);

            // Filtering and sorting are covered too: either one can be used to work out a value that is not selected.
            var sensitiveProperty = (parameter.RequestedPropertyNames ?? [])
                .Concat(parameter.Filtering?.Criterias?.Select(criteria => criteria.FilterBy) ?? [])
                .Append(parameter.Sorting?.SortBy)
                .Any(IsSensitiveProperty);

            if (permissionMissing || sensitiveProperty)
                HttpContext.Response.ThrowWithForbidden();
        }
    }

    private static bool IsSensitiveProperty(string propertyName)
        => propertyName is not null && _sensitivePropertyFragments.Any(fragment => propertyName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
