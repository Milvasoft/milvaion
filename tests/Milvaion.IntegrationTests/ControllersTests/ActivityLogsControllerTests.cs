using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.ActivityLogDtos;
using Milvaion.Application.Features.ActivityLogs.GetActivityLogList;
using Milvaion.Application.Utils.Constants;
using Milvaion.Domain;
using Milvaion.Domain.Enums;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for ActivityLogsController.")]
public class ActivityLogsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/activitylogs";

    [Fact]
    public async Task GetActivityLogsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetActivityLogListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetActivityLogsAsync_WithAuthorization_ShouldReturnActivityLogs()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedActivityLogsAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetActivityLogListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<ActivityLogListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCountGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetActivityLogsAsync_WithPagination_ShouldReturnPaginatedLogs()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedMultipleActivityLogsAsync(10);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetActivityLogListQuery
        {
            PageNumber = 1,
            RowCount = 5
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<ActivityLogListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(5);
        result.TotalDataCount.Should().Be(10);
    }

    [Fact]
    public async Task GetActivityLogsAsync_EmptyDatabase_ShouldReturnEmptyList()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetActivityLogListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<ActivityLogListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    #region Helper Methods

    private async Task SeedActivityLogsAsync()
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var logs = new List<ActivityLog>
        {
            new()
            {
                UserName = "testuser",
                Activity = UserActivity.CreateUser,
                ActivityDate = DateTimeOffset.UtcNow.AddHours(-1)
            },
            new()
            {
                UserName = "testuser",
                Activity = UserActivity.UpdateUser,
                ActivityDate = DateTimeOffset.UtcNow.AddMinutes(-30)
            },
            new()
            {
                UserName = "admin",
                Activity = UserActivity.CreateRole,
                ActivityDate = DateTimeOffset.UtcNow.AddMinutes(-15)
            }
        };

        await dbContext.ActivityLogs.AddRangeAsync(logs);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedMultipleActivityLogsAsync(int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var activities = Enum.GetValues<UserActivity>();
        for (int i = 0; i < count; i++)
        {
            var log = new ActivityLog
            {
                UserName = $"user{i + 1}",
                Activity = activities[i % activities.Length],
                ActivityDate = DateTimeOffset.UtcNow.AddMinutes(-i * 10)
            };
            await dbContext.ActivityLogs.AddAsync(log);
        }

        await dbContext.SaveChangesAsync();
    }

    #endregion
}
