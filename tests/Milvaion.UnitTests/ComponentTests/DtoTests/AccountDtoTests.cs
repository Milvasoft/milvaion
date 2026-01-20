using FluentAssertions;
using Microsoft.EntityFrameworkCore.Query;
using Milvaion.Application.Dtos;
using Milvaion.Application.Dtos.AccountDtos;
using Milvaion.Application.Dtos.UIDtos.MenuItemDtos;
using Milvaion.Application.Dtos.UIDtos.PageDtos;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvasoft.Identity.Concrete;
using System.Linq.Expressions;

namespace Milvaion.UnitTests.ComponentTests.DtoTests;

[Trait("DTO Unit Tests", "DTO models property and method unit tests.")]
public class AccountDtoTests
{
    [Fact]
    public void AccountDetailDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var id = 1;
        var userName = "TestUserName";
        var email = "test@example.com";
        var name = "TestName";
        var surname = "TestSurname";
        var roles = new List<NameIntNavigationDto>
        {
            new() { Id = 1, Name = "Admin" },
            new() { Id = 2, Name = "User" }
        };

        // Act
        var accountDetailDto = new AccountDetailDto
        {
            Id = id,
            UserName = userName,
            Email = email,
            Name = name,
            Surname = surname,
            Roles = roles
        };

        // Assert
        accountDetailDto.Id.Should().Be(id);
        accountDetailDto.UserName.Should().Be(userName);
        accountDetailDto.Email.Should().Be(email);
        accountDetailDto.Name.Should().Be(name);
        accountDetailDto.Surname.Should().Be(surname);
        accountDetailDto.Roles.Should().BeEquivalentTo(roles);
    }

    [Fact]
    public void AccountDetailDto_Projection_ShouldReturnCorrectly()
    {
        // Arrange
        Expression<Func<User, AccountDetailDto>> expectedExpression = u => new AccountDetailDto
        {
            Id = u.Id,
            UserName = u.UserName,
            Email = u.Email,
            Name = u.Name,
            Surname = u.Surname,
            Roles = u.RoleRelations.Select(rr => new NameIntNavigationDto { Id = rr.Role.Id, Name = rr.Role.Name }).ToList()
        };

        // Act
        var result = AccountDetailDto.Projection;

        // Assert
        var equality = ExpressionEqualityComparer.Instance.Equals(expectedExpression, result);

        equality.Should().BeTrue();
    }

    [Fact]
    public void LoginResponseDto_PropertyGetterSetter_ShouldWorkCorrectly()
    {
        // Arrange
        var userType = UserType.Manager;
        var userTypeDescription = "Manager user type";
        var token = new MilvaToken { AccessToken = "accessToken", RefreshToken = "refreshToken" };
        var accessibleMenuItems = new List<MenuItemDto> { new() { Name = "Home", Url = "/home" } };
        var pageInformations = new List<PageDto> { new() { Name = "Dashboard", LocalizedName = "Dashboard" } };

        // Act
        var loginResponseDto = new LoginResponseDto
        {
            UserType = userType,
            UserTypeDescription = userTypeDescription,
            Token = token,
            AccessibleMenuItems = accessibleMenuItems,
            PageInformations = pageInformations,
        };

        // Assert
        loginResponseDto.UserType.Should().Be(userType);
        loginResponseDto.UserTypeDescription.Should().Be(userTypeDescription);
        loginResponseDto.Token.Should().Be(token);
        loginResponseDto.AccessibleMenuItems.Should().BeEquivalentTo(accessibleMenuItems);
        loginResponseDto.PageInformations.Should().BeEquivalentTo(pageInformations);
    }
}
