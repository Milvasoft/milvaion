using FluentAssertions;
using Milvaion.Application.Dtos.AdminDtos;
using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for AdminController.")]
public class AdminControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/admin";

    #region System Health

    [Fact]
    public async Task GetSystemHealthAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/system-health");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSystemHealthAsync_WithAuthorization_ShouldReturnSystemHealth()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/system-health");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<SystemHealthInfo>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #endregion

    #region Configuration

    [Fact]
    public async Task GetConfigurationAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/configuration");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetConfigurationAsync_WithAuthorization_ShouldReturnConfiguration()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/configuration");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<SystemConfigurationDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #endregion

    #region Job Statistics

    [Fact]
    public async Task GetJobStatisticsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/job-stats");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetJobStatisticsAsync_WithAuthorization_ShouldReturnStatistics()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/job-stats");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<JobStatistics>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #endregion

    #region Redis Circuit Breaker

    [Fact]
    public async Task GetRedisCircuitBreakerStats_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/redis-circuit-breaker");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRedisCircuitBreakerStats_WithAuthorization_ShouldReturnStats()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/redis-circuit-breaker");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<RedisCircuitBreakerStatsDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #endregion

    #region Database Statistics

    [Fact]
    public async Task GetDatabaseStatisticsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/database-statistics");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDatabaseStatisticsAsync_WithAuthorization_ShouldReturnStatistics()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/database-statistics");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<DatabaseStatisticsDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #endregion

    #region Dispatcher Control

    [Fact]
    public async Task EmergencyStop_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().PostAsync($"{_baseUrl}/jobdispatcher/stop", null);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EmergencyStop_WithAuthorization_ShouldStopDispatcher()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.PostAsync($"{_baseUrl}/jobdispatcher/stop?reason=TestStop", null);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task ResumeOperations_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().PostAsync($"{_baseUrl}/jobdispatcher/resume", null);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ResumeOperations_WithAuthorization_ShouldResumeDispatcher()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.PostAsync($"{_baseUrl}/jobdispatcher/resume", null);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion
}
