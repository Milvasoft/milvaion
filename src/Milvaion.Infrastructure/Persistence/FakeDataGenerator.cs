using Bogus;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvasoft.Identity.Concrete;

namespace Milvaion.Infrastructure.Persistence;

/// <summary>
/// Data faker.
/// </summary>
#pragma warning disable S1699 // Constructors should only call non-overridable methods
public class UserFaker : Faker<User>
{
    /// <summary>
    /// Constructor for faker.
    /// </summary>
    /// <param name="sameData"></param>
    /// <param name="locale"></param>
    /// <param name="roles"></param>
    public UserFaker(bool sameData = true, string locale = "tr", List<Role> roles = null) : base(locale)
    {
        if (sameData)
            UseSeed(8675309);

        var id = 100;

        var passwordHasher = new MilvaPasswordHasher();

        RuleFor(u => u.Id, _ => id++)
            .RuleFor(u => u.UserName, (f, u) => f.Internet.UserName(u.Name, u.Surname))
            .RuleFor(u => u.Name, f => f.Name.FirstName())
            .RuleFor(u => u.Surname, f => f.Name.LastName())
            .RuleFor(u => u.UserType, f => f.PickRandom<UserType>())
            .RuleFor(u => u.Email, (f, u) => f.Internet.Email(u.Name, u.Surname))
            .RuleFor(u => u.PhoneNumber, f => f.Phone.PhoneNumber())
            .RuleFor(u => u.PasswordHash, f => passwordHasher.HashPassword(f.Internet.Password()))
            .RuleFor(u => u.RoleRelations, (f, u) => roles?.Select(r => new UserRoleRelation
            {
                RoleId = r.Id,
                UserId = u.Id
            }).ToList())
            ;
    }
}

/// <summary>
/// Data faker.
/// </summary>
public class RoleFaker : Faker<Role>
{
    private readonly List<int> _permissionIdList =
    [
        1, 21, 22, 23, 24, 25, 26, 27, 28, 29,
        30, 31, 32, 33, 34, 35, 36, 37, 38, 39,
        40, 41, 42, 43, 44, 45, 46, 47, 48, 49,
        50, 51, 52, 53, 54, 55, 56, 57, 58, 59,
        60, 61, 62, 63, 64, 65, 66, 67, 68, 69,
        70, 71, 72, 73, 74, 75, 76, 77, 78, 79,
        80
    ];

    /// <summary>
    /// Constructor for faker.
    /// </summary>
    /// <param name="sameData"></param>
    /// <param name="locale"></param>
    public RoleFaker(bool sameData = true, string locale = "tr") : base(locale)
    {
        if (sameData)
            UseSeed(8675309);

        var id = 100;

        RuleFor(u => u.Id, _ => id++)
            .RuleFor(u => u.Name, f => f.Commerce.Department())
            .RuleFor(u => u.RolePermissionRelations, (f, r) => [.. PermissionCatalog.GetPermissionsAndGroups().SelectMany(p => p.Value).Select(p => new RolePermissionRelation
            {
                RoleId = r.Id,
                PermissionId = f.PickRandom(_permissionIdList)
            }).DistinctBy(i => i.PermissionId)])
            ;
    }
}
#pragma warning restore S1699 // Constructors should only call non-overridable methods