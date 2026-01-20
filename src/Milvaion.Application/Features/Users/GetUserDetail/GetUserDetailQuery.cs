using Milvaion.Application.Dtos.UserDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Users.GetUserDetail;

/// <summary>
/// Data transfer object for user details.
/// </summary>
public record GetUserDetailQuery : IQuery<UserDetailDto>
{
    /// <summary>
    /// Id of the user whose details will be accessed.
    /// </summary>
    public int UserId { get; set; }
}
