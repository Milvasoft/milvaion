using FluentAssertions;
using Milvaion.Application.Dtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.DataAccess.EfCore.Utils.LookupModels;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for LookupsController.")]
public class LookupsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/lookups";

    #region GetLookups

    [Fact]
    public async Task GetLookupsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new LookupRequest
        {
            Parameters =
            [
                new()
                {
                    EntityName = "User",
                    RequestedPropertyNames = ["Name"]
                }
            ]
        };

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetLookupsAsync_WithAuthorization_ShouldReturnLookups()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new LookupRequest
        {
            Parameters =
            [
                new()
                {
                    EntityName = "User",
                    RequestedPropertyNames = ["Name"]
                }
            ]
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<object>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetLookupsAsync_WithInvalidEntityName_ShouldReturnEmptyResult()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new LookupRequest
        {
            Parameters =
            [
                new()
                {
                    EntityName = "NonExistentEntity",
                    RequestedPropertyNames = ["Name"]
                }
            ]
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<object>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task GetLookupsAsync_WithRoleLookup_ShouldReturnRoles()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new LookupRequest
        {
            Parameters =
            [
                new()
                {
                    EntityName = "Role",
                    RequestedPropertyNames = ["Id", "Name"]
                }
            ]
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<object>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region GetEnumLookups

    [Fact]
    public async Task GetEnumLookups_WithValidEnumName_ShouldReturnEnumValues()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var enumName = "UserType";

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/enum?enumName={enumName}");
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<EnumLookupModel>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetEnumLookups_WithInvalidEnumName_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var enumName = "NonExistentEnum";

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/enum?enumName={enumName}");
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<EnumLookupModel>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetEnumLookups_WithEmptyEnumName_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/enum?enumName=");
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<EnumLookupModel>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task GetEnumLookups_WithUserActivityEnum_ShouldReturnActivities()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var enumName = "UserActivity";

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/enum?enumName={enumName}");
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<EnumLookupModel>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeEmpty();
    }

    #endregion
}
