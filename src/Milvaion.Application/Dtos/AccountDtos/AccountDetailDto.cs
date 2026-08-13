using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.AccountDtos;

/// <summary>
/// Data transfer object for account details.
/// </summary>
[Translate]
[ExcludeFromMetadata]
public class AccountDetailDto : MilvaionBaseDto<int>
{
    /// <summary>
    /// Unique username of the user. 
    /// </summary>
    public string UserName { get; set; }

    /// <summary>
    /// Email of the user.
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// Name of the user.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Surname of the user.
    /// </summary>
    public string Surname { get; set; }

    /// <summary>
    /// Roles the user belongs to.
    /// </summary>
    public List<NameIntNavigationDto> Roles { get; set; }

    /// <summary>
    /// Where this account is managed. <see cref="ExternalProvider.Local"/> means Milvaion owns the
    /// credentials; otherwise identity and password are owned by the external provider (OIDC/LDAP).
    /// </summary>
    public ExternalProvider Provider { get; set; }

    /// <summary>
    /// Projection expression for mapping User entity to AccountDetailDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<User, AccountDetailDto>> Projection { get; } = u => new AccountDetailDto
    {
        Id = u.Id,
        UserName = u.UserName,
        Email = u.Email,
        Name = u.Name,
        Surname = u.Surname,
        Provider = u.Provider,
        Roles = u.RoleRelations.Select(rr => new NameIntNavigationDto { Id = rr.Role.Id, Name = rr.Role.Name }).ToList()
    };
}
