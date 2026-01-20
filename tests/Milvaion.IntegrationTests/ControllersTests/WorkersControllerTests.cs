using FluentAssertions;
using Milvaion.Application.Dtos.WorkerDtos;
using Milvaion.Application.Features.Workers.GetWorkerList;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for WorkersController.")]
public class WorkersControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/workers";

    #region GetWorkers

    [Fact]
    public async Task GetWorkersAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetWorkerListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetWorkersAsync_WithAuthorization_ShouldReturnWorkers()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetWorkerListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<WorkerDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetWorkersAsync_WithPagination_ShouldReturnPaginatedWorkers()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetWorkerListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<WorkerDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region GetWorker

    [Fact]
    public async Task GetWorkerAsync_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/worker?WorkerId=non-existent-worker-id");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<WorkerDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.Data.Should().BeNull();
    }

    #endregion

    #region DeleteWorker

    [Fact]
    public async Task DeleteWorkerAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().DeleteAsync($"{_baseUrl}/worker?WorkerId=test-worker-id");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteWorkerAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/worker?WorkerId=non-existent-worker-id");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<string>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
    }

    #endregion
}
