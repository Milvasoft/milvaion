using FluentAssertions;
using Milvaion.Application.Dtos.HealthDtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.IntegrationTests.TestBase;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for HealthCheckController.")]
public class HealthCheckControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/healthcheck";

    #region Health Check

    [Fact]
    public async Task HealthCheck_ShouldReturnOk()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync(_baseUrl);
        var result = await httpResponse.Content.ReadAsStringAsync();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().Be("Ok");
    }

    #endregion

    #region Readiness Probe

    [Fact]
    public async Task ReadinessProbe_ShouldReturnHealthCheckResponse()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/ready");
        var result = await httpResponse.Content.ReadFromJsonAsync<HealthCheckResponse>();

        // Assert
        result.Should().NotBeNull();
        result.Status.Should().NotBeNullOrEmpty();
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ReadinessProbe_ShouldContainChecks()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/ready");
        var result = await httpResponse.Content.ReadFromJsonAsync<HealthCheckResponse>();

        // Assert
        result.Should().NotBeNull();
        result.Checks.Should().NotBeNull();
    }

    #endregion

    #region Liveness Probe

    [Fact]
    public async Task LivenessProbe_ShouldReturnHealthyStatus()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/live");
        var result = await httpResponse.Content.ReadFromJsonAsync<LivenessResponse>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.Status.Should().Be("Healthy");
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        result.Uptime.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task LivenessProbe_ShouldReturnUptime()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/live");
        var result = await httpResponse.Content.ReadFromJsonAsync<LivenessResponse>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.Uptime.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    #endregion

    #region Startup Probe

    [Fact]
    public async Task StartupProbe_ShouldReturnStartedStatus()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/startup");
        var result = await httpResponse.Content.ReadFromJsonAsync<LivenessResponse>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.Status.Should().Be("Started");
        result.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task StartupProbe_ShouldReturnUptime()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/startup");
        var result = await httpResponse.Content.ReadFromJsonAsync<LivenessResponse>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.Uptime.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    #endregion

    #region Anonymous Access

    [Fact]
    public async Task AllEndpoints_ShouldBeAccessibleWithoutAuthentication()
    {
        // Act
        var healthResponse = await _factory.CreateClient().GetAsync(_baseUrl);
        var readyResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/ready");
        var liveResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/live");
        var startupResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/startup");

        // Assert - All should return 200 OK without authentication
        healthResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        liveResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        startupResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        // Readiness might return 503 if dependencies are unhealthy, so we just check it doesn't return 401/403
        readyResponse.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        readyResponse.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    #endregion
}
