using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Application.Dtos;
using Milvaion.Application.Dtos.RoleDtos;
using Milvaion.Domain;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class RoleDtoTests
{
    [Fact]
    public void RoleDetailDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "Editor";
        var users = new List<NameIntNavigationDto> { new() { Id = 1, Name = "User1" } };
        var permissions = new List<NameIntNavigationDto> { new() { Id = 1, Name = "Permission1" } };
        var auditInfo = new AuditDto<int>(new Role { Id = id, Name = name });

        // Act
        var roleDetailDto = new RoleDetailDto
        {
            Id = id,
            Name = name,
            Users = users,
            Permissions = permissions,
            AuditInfo = auditInfo
        };

        // Assert
        roleDetailDto.Id.Should().Be(id);
        roleDetailDto.Name.Should().Be(name);
        roleDetailDto.Users.Should().BeEquivalentTo(users);
        roleDetailDto.Permissions.Should().BeEquivalentTo(permissions);
        roleDetailDto.AuditInfo.Should().Be(auditInfo);
    }

    [Fact]
    public void RoleDetailDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Role, RoleDetailDto>> expectedExpression = r => new RoleDetailDto
        {
            Id = r.Id,
            Name = r.Name,
            Permissions = r.RolePermissionRelations.Select(p => new NameIntNavigationDto
            {
                Id = p.PermissionId,
                Name = p.Permission.Name,
            }).ToList(),
            Users = r.UserRoleRelations.Select(p => new NameIntNavigationDto
            {
                Id = p.UserId,
                Name = p.User.UserName,
            }).ToList(),
            AuditInfo = new AuditDto<int>(r)
        };

        // Act
        var result = RoleDetailDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void RoleListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var name = "Admin";

        // Act
        var roleListDto = new RoleListDto
        {
            Id = id,
            Name = name
        };

        // Assert
        roleListDto.Id.Should().Be(id);
        roleListDto.Name.Should().Be(name);
    }

    [Fact]
    public void RoleListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<Role, RoleListDto>> expectedExpression = r => new RoleListDto
        {
            Id = r.Id,
            Name = r.Name
        };

        // Act
        var result = RoleListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }
}
