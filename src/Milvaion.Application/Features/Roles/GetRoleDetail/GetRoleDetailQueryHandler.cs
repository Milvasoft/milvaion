using Milvaion.Application.Dtos.RoleDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Roles.GetRoleDetail;

/// <summary>
/// Handles the role detail operation.
/// </summary>
/// <param name="roleRepository"></param>
public class GetRoleDetailQueryHandler(IMilvaionRepositoryBase<Role> roleRepository) : IInterceptable, IQueryHandler<GetRoleDetailQuery, RoleDetailDto>
{
    private readonly IMilvaionRepositoryBase<Role> _roleRepository = roleRepository;

    /// <inheritdoc/>
    public async Task<Response<RoleDetailDto>> Handle(GetRoleDetailQuery request, CancellationToken cancellationToken)
    {
        var role = await _roleRepository.GetByIdAsync(request.RoleId, projection: RoleDetailDto.Projection, cancellationToken: cancellationToken);

        if (role == null)
            return Response<RoleDetailDto>.Success(role, MessageKey.RoleNotFound, MessageType.Warning);

        return Response<RoleDetailDto>.Success(role);
    }
}
