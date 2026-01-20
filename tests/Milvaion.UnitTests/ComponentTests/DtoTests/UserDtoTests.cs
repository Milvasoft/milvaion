using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Application.Dtos;
using Milvaion.Application.Dtos.UserDtos;
using Milvaion.Domain;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class UserDtoTests
{
    [Fact]
    public void UserDetailDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var userName = "TestUserName";
        var email = "test@example.com";
        var name = "TestName";
        var surname = "TestSurname";
        var roles = new List<NameIntNavigationDto> { new() { Id = 1, Name = "Admin" } };
        var auditInfo = new AuditDto<int>(new User { Id = id });

        // Act
        var userDetailDto = new UserDetailDto
        {
            Id = id,
            UserName = userName,
            Email = email,
            Name = name,
            Surname = surname,
            Roles = roles,
            AuditInfo = auditInfo
        };

        // Assert
        userDetailDto.Id.Should().Be(id);
        userDetailDto.UserName.Should().Be(userName);
        userDetailDto.Email.Should().Be(email);
        userDetailDto.Name.Should().Be(name);
        userDetailDto.Surname.Should().Be(surname);
        userDetailDto.Roles.Should().BeEquivalentTo(roles);
        userDetailDto.AuditInfo.Should().Be(auditInfo);
    }

    [Fact]
    public void UserDetailDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, UserDetailDto>> expectedExpression = u => new UserDetailDto
        {
            Id = u.Id,
            UserName = u.UserName,
            Email = u.Email,
            Name = u.Name,
            Surname = u.Surname,
            Roles = u.RoleRelations.Select(rr => new NameIntNavigationDto { Id = rr.Role.Id, Name = rr.Role.Name }).ToList(),
            AllowedNotifications = u.AllowedNotifications,
            AuditInfo = new AuditDto<int>(u)
        };

        // Act
        var result = UserDetailDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void UserListDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var userName = "TestUserName";
        var email = "test@example.com";
        var name = "TestName";
        var surname = "TestSurname";

        // Act
        var userListDto = new UserListDto
        {
            Id = id,
            UserName = userName,
            Email = email,
            Name = name,
            Surname = surname
        };

        // Assert
        userListDto.Id.Should().Be(id);
        userListDto.UserName.Should().Be(userName);
        userListDto.Email.Should().Be(email);
        userListDto.Name.Should().Be(name);
        userListDto.Surname.Should().Be(surname);
    }

    [Fact]
    public void UserListDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, UserListDto>> expectedExpression = u => new UserListDto
        {
            Id = u.Id,
            UserName = u.UserName,
            Email = u.Email,
            Name = u.Name,
            Surname = u.Surname
        };

        // Act
        var result = UserListDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }
}
