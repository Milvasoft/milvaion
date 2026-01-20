using Milvaion.Application.Dtos.UserDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Users.GetUserList;

/// <summary>
/// Handles the user list operation.
/// </summary>
/// <param name="userRepository"></param>
public class GetUserListQueryHandler(IMilvaionRepositoryBase<User> userRepository) : IInterceptable, IListQueryHandler<GetUserListQuery, UserListDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<UserListDto>> Handle(GetUserListQuery request, CancellationToken cancellationToken)
    {
        var response = await _userRepository.GetAllAsync(request, projection: UserListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}