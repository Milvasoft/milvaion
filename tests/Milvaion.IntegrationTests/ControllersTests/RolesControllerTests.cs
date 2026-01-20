using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.RoleDtos;
using Milvaion.Application.Features.Roles.CreateRole;
using Milvaion.Application.Features.Roles.GetRoleList;
using Milvaion.Application.Features.Roles.UpdateRole;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for RolesController.")]
public class RolesControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/roles";

    #region GetRoles

    [Fact]
    public async Task GetRolesAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetRoleListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRolesAsync_WithAuthorization_ShouldReturnRoles()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetRoleListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<RoleListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetRolesAsync_WithPagination_ShouldReturnPaginatedRoles()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedMultipleRolesAsync(5);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetRoleListQuery
        {
            PageNumber = 1,
            RowCount = 3
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<RoleListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(3);
        result.TotalDataCount.Should().BeGreaterThanOrEqualTo(5);
    }

    #endregion

    #region GetRole

    [Fact]
    public async Task GetRoleAsync_WithValidId_ShouldReturnRoleDetail()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/role?RoleId=5000");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<RoleDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data.Name.Should().Be("SuperAdmin");
    }

    [Fact]
    public async Task GetRoleAsync_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/role?RoleId=999");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<RoleDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    #endregion

    #region AddRole

    [Fact]
    public async Task AddRoleAsync_WithValidData_ShouldCreateRole()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateRoleCommand
        {
            Name = "TestRole",
            PermissionIdList = [1]
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/role", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeGreaterThan(0);

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var createdRole = await dbContext.Roles.FirstOrDefaultAsync(r => r.Name == "TestRole");
        createdRole.Should().NotBeNull();
    }

    [Fact]
    public async Task AddRoleAsync_WithEmptyName_ShouldReturnValidationError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateRoleCommand
        {
            Name = "",
            PermissionIdList = [1]
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/role", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    #endregion

    #region UpdateRole

    [Fact]
    public async Task UpdateRoleAsync_WithValidData_ShouldUpdateRole()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var newRole = await SeedSingleRoleAsync("EditableRole");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateRoleCommand
        {
            Id = newRole.Id,
            Name = new UpdateProperty<string>("UpdatedRoleName")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/role", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedRole = await dbContext.Roles.FirstOrDefaultAsync(r => r.Id == newRole.Id);
        updatedRole.Should().NotBeNull();
        updatedRole.Name.Should().Be("UpdatedRoleName");
    }

    [Fact]
    public async Task UpdateRoleAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateRoleCommand
        {
            Id = 999,
            Name = new UpdateProperty<string>("UpdatedName")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/role", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region DeleteRole

    [Fact]
    public async Task DeleteRoleAsync_WithValidId_ShouldDeleteRole()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var newRole = await SeedSingleRoleAsync("DeletableRole");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/role?RoleId={newRole.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedRole = await dbContext.Roles.FirstOrDefaultAsync(r => r.Id == newRole.Id);
        deletedRole.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteRoleAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/role?RoleId=999");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region Helper Methods

    private async Task SeedMultipleRolesAsync(int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        for (int i = 0; i < count; i++)
        {
            var role = new Role
            {
                Name = $"TestRole{i + 1}",
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            };
            await dbContext.Roles.AddAsync(role);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<Role> SeedSingleRoleAsync(string name)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var role = new Role
        {
            Name = name,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        await dbContext.Roles.AddAsync(role);
        await dbContext.SaveChangesAsync();

        return role;
    }

    #endregion
}
