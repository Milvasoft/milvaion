using Milvaion.Application.Dtos.PermissionDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Abstractions.Localization;

namespace Milvaion.Application.Features.Permissions.GetPermissionList;

/// <summary>
/// Handles the permission list operation.
/// </summary>
/// <param name="permissionRepository"></param>
/// <param name="milvaLocalizer"></param>
public class GetPermissionListQueryHandler(IMilvaionRepositoryBase<Permission> permissionRepository, IMilvaLocalizer milvaLocalizer) : IInterceptable, IListQueryHandler<GetPermissionListQuery, PermissionListDto>
{
    private readonly IMilvaionRepositoryBase<Permission> _permissionRepository = permissionRepository;
    private readonly IMilvaLocalizer _milvaLocalizer = milvaLocalizer;

    /// <inheritdoc/>
    public async Task<ListResponse<PermissionListDto>> Handle(GetPermissionListQuery request, CancellationToken cancellationToken)
    {
        var response = await _permissionRepository.GetAllAsync(request, projection: PermissionListDto.Projection, cancellationToken: cancellationToken);

        response.Data.ForEach(response =>
        {
            response.Description = _milvaLocalizer[$"PG.{response.Name}", _milvaLocalizer[response.PermissionGroup]];
            response.PermissionGroupDescription = _milvaLocalizer[$"PG.{response.PermissionGroup}"];
        });

        return response;
    }
}
