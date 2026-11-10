using FluentAssertions;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.IntegrationTests.TestBase;
using System.Net;
using System.Text;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

/// <summary>
/// Pins that endpoints refuse an authenticated caller who lacks the required permission. Every other controller
/// test logs in as a super admin, so a dropped <c>[Auth(...)]</c> would otherwise go unnoticed.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for permission enforcement.")]
public class PermissionEnforcementTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0";

    [Theory]
    [InlineData("PATCH", "jobs")]
    [InlineData("POST", "jobs/job")]
    [InlineData("PUT", "jobs/job")]
    [InlineData("DELETE", "jobs/job")]
    [InlineData("POST", "jobs/job/trigger")]
    [InlineData("PATCH", "users")]
    [InlineData("POST", "users/user")]
    [InlineData("DELETE", "users/user")]
    [InlineData("PATCH", "roles")]
    [InlineData("POST", "roles/role")]
    [InlineData("PUT", "roles/role")]
    [InlineData("PATCH", "workers")]
    [InlineData("DELETE", "workers/worker")]
    [InlineData("PATCH", "apikeys")]
    [InlineData("POST", "apikeys/apiKey")]
    [InlineData("POST", "apikeys/apiKey/revoke")]
    public async Task Endpoint_WithoutRequiredPermission_ShouldReturnForbidden(string method, string path)
    {
        // Arrange
        await SeedUserWithPermissionsAsync("nopermissionuser");
        var client = await _factory.CreateClient().LoginAsync("nopermissionuser");

        // Act
        var httpResponse = await SendAsync(client, method, path);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ReadOnlyUser_ShouldListJobs_ButNotCreateThem()
    {
        // Arrange
        await SeedUserWithPermissionsAsync("readonlyuser", permissions: PermissionCatalog.ScheduledJobManagement.List);
        var client = await _factory.CreateClient().LoginAsync("readonlyuser");

        // Act
        var listResponse = await SendAsync(client, "PATCH", "jobs");
        var createResponse = await SendAsync(client, "POST", "jobs/job");
        var otherAreaResponse = await SendAsync(client, "PATCH", "users");

        // Assert
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        otherAreaResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string path) => client.SendAsync(new HttpRequestMessage(new HttpMethod(method), $"{_baseUrl}/{path}")
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    });
}
