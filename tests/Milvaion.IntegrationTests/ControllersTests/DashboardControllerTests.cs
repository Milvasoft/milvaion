using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.DashboardDtos;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for DashboardController.")]
public class DashboardControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/dashboard";

    [Fact]
    public async Task GetDashboardAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync(_baseUrl);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetDashboardAsync_WithAuthorization_ShouldReturnDashboardData()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync(_baseUrl);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<DashboardDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDashboardAsync_WithScheduledJobs_ShouldReturnJobStatistics()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedDashboardDataAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync(_baseUrl);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<DashboardDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task GetDashboardAsync_WithJobOccurrences_ShouldReturnOccurrenceStatistics()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedDashboardDataWithOccurrencesAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync(_baseUrl);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<DashboardDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    #region Helper Methods

    private async Task SeedDashboardDataAsync()
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var jobs = new List<ScheduledJob>
        {
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "ActiveJob1",
                JobNameInWorker = "TestJob",
                ExecuteAt = DateTime.UtcNow.AddHours(1),
                IsActive = true,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            },
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "ActiveJob2",
                JobNameInWorker = "TestJob",
                ExecuteAt = DateTime.UtcNow.AddHours(2),
                IsActive = true,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            },
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "InactiveJob",
                JobNameInWorker = "TestJob",
                ExecuteAt = DateTime.UtcNow.AddHours(3),
                IsActive = false,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            }
        };

        await dbContext.ScheduledJobs.AddRangeAsync(jobs);
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedDashboardDataWithOccurrencesAsync()
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            DisplayName = "JobWithOccurrences",
            JobNameInWorker = "TestJob",
            ExecuteAt = DateTime.UtcNow.AddHours(1),
            IsActive = true,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        await dbContext.ScheduledJobs.AddAsync(job);

        var occurrences = new List<JobOccurrence>
        {
            new()
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                CorrelationId = Guid.NewGuid(),
                Status = JobOccurrenceStatus.Completed,
                CreatedAt = DateTime.UtcNow,
                CreationDate = DateTime.UtcNow
            },
            new()
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                CorrelationId = Guid.NewGuid(),
                Status = JobOccurrenceStatus.Running,
                CreatedAt = DateTime.UtcNow,
                CreationDate = DateTime.UtcNow
            },
            new()
            {
                Id = Guid.NewGuid(),
                JobId = job.Id,
                CorrelationId = Guid.NewGuid(),
                Status = JobOccurrenceStatus.Failed,
                CreatedAt = DateTime.UtcNow,
                CreationDate = DateTime.UtcNow
            }
        };

        await dbContext.JobOccurrences.AddRangeAsync(occurrences);
        await dbContext.SaveChangesAsync();
    }

    #endregion
}
