using Milvaion.Application.Dtos.RoleDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Roles.GetRoleList;

/// <summary>
/// Handles the role list operation.
/// </summary>
/// <param name="roleRepository"></param>
public class GetRoleListQueryHandler(IMilvaionRepositoryBase<Role> roleRepository) : IInterceptable, IListQueryHandler<GetRoleListQuery, RoleListDto>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = roleRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<RoleListDto>> Handle(GetRoleListQuery request, CancellationToken cancellationToken)
    {
        var response = await _roleRepository.GetAllAsync(request, projection: RoleListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
