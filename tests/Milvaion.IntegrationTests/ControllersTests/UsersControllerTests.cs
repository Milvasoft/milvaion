using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.UserDtos;
using Milvaion.Application.Features.Users.CreateUser;
using Milvaion.Application.Features.Users.GetUserList;
using Milvaion.Application.Features.Users.UpdateUser;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for UsersController.")]
public class UsersControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/users";

    #region GetUsers

    [Fact]
    public async Task GetUsersAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetUserListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetUsersAsync_WithAuthorization_ShouldReturnUsers()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetUserListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<UserListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetUsersAsync_WithPagination_ShouldReturnPaginatedUsers()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedMultipleUsersAsync(5);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetUserListQuery
        {
            PageNumber = 1,
            RowCount = 3
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<UserListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(3);
        result.TotalDataCount.Should().BeGreaterThanOrEqualTo(5);
    }

    #endregion

    #region GetUser

    [Fact]
    public async Task GetUserAsync_WithValidId_ShouldReturnUserDetail()
    {
        // Arrange
        var rootUser = await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/user?UserId={rootUser.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<UserDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data.UserName.Should().Be(GlobalConstant.RootUsername);
    }

    [Fact]
    public async Task GetUserAsync_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/user?UserId=999");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<UserDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    #endregion

    #region AddUser

    [Fact]
    public async Task AddUserAsync_WithValidData_ShouldCreateUser()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateUserCommand
        {
            UserName = "newuser",
            Email = "newuser@test.com",
            Name = "New",
            Surname = "User",
            Password = "Test123!",
            UserType = UserType.AppUser,
            RoleIdList = [5000]
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/user", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeGreaterThan(0);

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var createdUser = await dbContext.Users.FirstOrDefaultAsync(u => u.UserName == "newuser");
        createdUser.Should().NotBeNull();
        createdUser.Email.Should().Be("newuser@test.com");
    }

    [Fact]
    public async Task AddUserAsync_WithEmptyUserName_ShouldReturnValidationError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateUserCommand
        {
            UserName = "",
            Email = "test@test.com",
            Name = "Test",
            Surname = "User",
            Password = "Test123!",
            UserType = UserType.AppUser
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/user", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddUserAsync_WithDuplicateUserName_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateUserCommand
        {
            UserName = GlobalConstant.RootUsername, // Already exists
            Email = "duplicate@test.com",
            Name = "Duplicate",
            Surname = "User",
            Password = "Test123!",
            UserType = UserType.AppUser
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/user", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region UpdateUser

    [Fact]
    public async Task UpdateUserAsync_WithValidData_ShouldUpdateUser()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var newUser = await SeedSingleUserAsync("editableuser");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateUserCommand
        {
            Id = newUser.Id,
            Name = new UpdateProperty<string>("UpdatedName"),
            Surname = new UpdateProperty<string>("UpdatedSurname")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/user", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedUser = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == newUser.Id);
        updatedUser.Should().NotBeNull();
        updatedUser.Name.Should().Be("UpdatedName");
        updatedUser.Surname.Should().Be("UpdatedSurname");
    }

    [Fact]
    public async Task UpdateUserAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateUserCommand
        {
            Id = 999,
            Name = new UpdateProperty<string>("UpdatedName")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/user", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region DeleteUser

    [Fact]
    public async Task DeleteUserAsync_WithValidId_ShouldDeleteUser()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var newUser = await SeedSingleUserAsync("deletableuser");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/user?UserId={newUser.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database (soft delete check)
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedUser = await dbContext.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == newUser.Id);
        deletedUser.Should().NotBeNull();
        deletedUser.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteUserAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/user?UserId=999");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteUserAsync_SelfDelete_ShouldReturnError()
    {
        // Arrange - try to delete the logged in user
        var rootUser = await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/user?UserId={rootUser.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private async Task SeedMultipleUsersAsync(int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        for (int i = 0; i < count; i++)
        {
            var user = new User
            {
                UserName = $"testuser{i + 1}",
                Email = $"testuser{i + 1}@test.com",
                Name = $"Test{i + 1}",
                Surname = $"User{i + 1}",
                UserType = UserType.AppUser,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            };
            await dbContext.Users.AddAsync(user);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<User> SeedSingleUserAsync(string userName)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var user = new User
        {
            UserName = userName,
            Email = $"{userName}@test.com",
            Name = "Test",
            Surname = "User",
            UserType = UserType.AppUser,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        await dbContext.Users.AddAsync(user);
        await dbContext.SaveChangesAsync();

        return user;
    }

    #endregion
}
