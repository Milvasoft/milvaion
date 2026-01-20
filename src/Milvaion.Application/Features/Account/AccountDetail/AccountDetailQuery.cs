using Milvaion.Application.Dtos.AccountDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Account.AccountDetail;

/// <summary>
/// Data transfer object for account details.
/// </summary>
public record AccountDetailQuery : IQuery<AccountDetailDto>
{
    /// <summary>
    /// The user Id whose account details you want to access.
    /// </summary>
    public int UserId { get; set; }
}
