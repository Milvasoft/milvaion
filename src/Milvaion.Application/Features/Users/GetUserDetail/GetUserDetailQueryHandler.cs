using Milvaion.Application.Dtos.UserDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Users.GetUserDetail;

/// <summary>
/// Handles the user detail operation.
/// </summary>
/// <param name="userRepository"></param>
public class GetUserDetailQueryHandler(IMilvaionRepositoryBase<User> userRepository) : IInterceptable, IQueryHandler<GetUserDetailQuery, UserDetailDto>
{
    private readonly IMilvaionRepositoryBase<User> _userRepository = userRepository;

    /// <inheritdoc/>
    public async Task<Response<UserDetailDto>> Handle(GetUserDetailQuery request, CancellationToken cancellationToken)
    {
        var user = await _userRepository.GetByIdAsync(request.UserId, projection: UserDetailDto.Projection, cancellationToken: cancellationToken);

        if (user == null)
            return Response<UserDetailDto>.Success(user, MessageKey.UserNotFound, MessageType.Warning);

        return Response<UserDetailDto>.Success(user);
    }
}
