using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.PermissionDtos;
using Milvaion.Application.Features.Permissions.GetPermissionList;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for PermissionsController.")]
public class PermissionsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/permissions";

    #region GetPermissions

    [Fact]
    public async Task GetPermissionsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetPermissionListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetPermissionsAsync_WithAuthorization_ShouldReturnPermissions()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedPermissionsAsync(5);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetPermissionListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<PermissionListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetPermissionsAsync_WithPagination_ShouldReturnPaginatedPermissions()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedPermissionsAsync(10);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetPermissionListQuery
        {
            PageNumber = 1,
            RowCount = 5
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<PermissionListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(5);
        result.TotalDataCount.Should().BeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task GetPermissionsAsync_EmptyDatabase_ShouldReturnOnlySuperAdminPermission()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetPermissionListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<PermissionListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
        result.Data.Should().Contain(p => p.Name == nameof(PermissionCatalog.App.SuperAdmin));
    }

    #endregion

    #region MigratePermissions

    [Fact]
    public async Task MigratePermissionsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().PutAsync($"{_baseUrl}/migrate", null);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task MigratePermissionsAsync_WithAuthorization_ShouldMigratePermissions()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.PutAsync($"{_baseUrl}/migrate", null);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<string>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private async Task SeedPermissionsAsync(int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        for (int i = 0; i < count; i++)
        {
            var permission = new Permission
            {
                Name = $"TestPermission{i + 1}",
                Description = $"Test permission {i + 1} description",
                NormalizedName = $"TESTPERMISSION{i + 1}",
                PermissionGroup = "TestGroup",
                PermissionGroupDescription = "Test group description"
            };
            await dbContext.Permissions.AddAsync(permission);
        }

        await dbContext.SaveChangesAsync();
    }

    #endregion
}
