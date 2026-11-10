using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.MetricReportDtos;
using Milvaion.Application.Features.MetricReports.GetMetricReportList;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Milvaion.Sdk.Domain;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for MetricReportsController.")]
public class MetricReportsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/metricreports";

    #region GetReports

    [Fact]
    public async Task GetReportsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetMetricReportListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetReportsAsync_WithAuthorization_ShouldReturnReports()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedMetricReportsAsync(3);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetMetricReportListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<MetricReportListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.StatusCode.Should().Be((int)HttpStatusCode.OK);
        result.Data.Should().NotBeNull();
        result.Data.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetReportsAsync_WithPagination_ShouldReturnPaginatedReports()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedMetricReportsAsync(5);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetMetricReportListQuery
        {
            PageNumber = 1,
            RowCount = 3
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<MetricReportListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(3);
        result.TotalDataCount.Should().Be(5);
    }

    [Fact]
    public async Task GetReportsAsync_WithMetricTypeFilter_ShouldReturnFilteredReports()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedMetricReportsAsync(3, "WorkerThroughput");
        await SeedSingleMetricReportAsync("FailureRateTrend", "Failure Rate Trend Report");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetMetricReportListQuery
        {
            MetricType = "FailureRateTrend"
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<MetricReportListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(1);
        result.Data[0].MetricType.Should().Be("FailureRateTrend");
    }

    [Fact]
    public async Task GetReportsAsync_WithNoData_ShouldReturnEmptyList()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetMetricReportListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<MetricReportListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    #endregion

    #region GetReportById

    [Fact]
    public async Task GetReportByIdAsync_WithValidId_ShouldReturnReportDetail()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var report = await SeedSingleMetricReportAsync("WorkerThroughput", "Worker Throughput Report");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}?Id={report.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<MetricReportDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data.MetricType.Should().Be("WorkerThroughput");
        result.Data.DisplayName.Should().Be("Worker Throughput Report");
    }

    [Fact]
    public async Task GetReportByIdAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}?Id={Guid.CreateVersion7()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<MetricReportDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeFalse();
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetReportByIdAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}?Id={Guid.CreateVersion7()}");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region GetLatestReportByType

    [Fact]
    public async Task GetLatestReportByTypeAsync_WithValidMetricType_ShouldReturnLatestReport()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedSingleMetricReportAsync("JobHealthScore", "Old Report", generatedAt: DateTime.UtcNow.AddDays(-5));
        var latestReport = await SeedSingleMetricReportAsync("JobHealthScore", "Latest Report", generatedAt: DateTime.UtcNow);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/latest?MetricType=JobHealthScore");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<MetricReportDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data.DisplayName.Should().Be("Latest Report");
        result.Data.Id.Should().Be(latestReport.Id);
    }

    [Fact]
    public async Task GetLatestReportByTypeAsync_WithNonExistentMetricType_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/latest?MetricType=NonExistentType");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<MetricReportDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeFalse();
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestReportByTypeAsync_WithEmptyMetricType_ShouldReturnValidationError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/latest?MetricType=");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<MetricReportDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetLatestReportByTypeAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/latest?MetricType=JobHealthScore");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region DeleteReport

    [Fact]
    public async Task DeleteReportAsync_WithValidId_ShouldDeleteReport()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var report = await SeedSingleMetricReportAsync("WorkerThroughput", "Report to delete");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}?Id={report.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(report.Id);

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedReport = await dbContext.MetricReports.FirstOrDefaultAsync(r => r.Id == report.Id);
        deletedReport.Should().BeNull();
    }

    [Fact]
    public async Task DeleteReportAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}?Id={Guid.CreateVersion7()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteReportAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().DeleteAsync($"{_baseUrl}?Id={Guid.CreateVersion7()}");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region DeleteOldReports

    [Fact]
    public async Task DeleteOldReportsAsync_WithOldReports_ShouldDeleteOldReports()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedSingleMetricReportAsync("WorkerThroughput", "Old Report 1", generatedAt: DateTime.UtcNow.AddDays(-60));
        await SeedSingleMetricReportAsync("JobHealthScore", "Old Report 2", generatedAt: DateTime.UtcNow.AddDays(-45));
        await SeedSingleMetricReportAsync("FailureRateTrend", "Recent Report", generatedAt: DateTime.UtcNow.AddDays(-5));
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/cleanup?OlderThanDays=30");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(2);

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var remainingReports = await dbContext.MetricReports.ToListAsync();
        remainingReports.Should().HaveCount(1);
        remainingReports[0].DisplayName.Should().Be("Recent Report");
    }

    [Fact]
    public async Task DeleteOldReportsAsync_WithNoOldReports_ShouldReturnZero()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedSingleMetricReportAsync("WorkerThroughput", "Recent Report", generatedAt: DateTime.UtcNow.AddDays(-5));
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/cleanup?OlderThanDays=30");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(0);
    }

    [Fact]
    public async Task DeleteOldReportsAsync_WithInvalidOlderThanDays_ShouldReturnValidationError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/cleanup?OlderThanDays=0");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteOldReportsAsync_WithExceedingOlderThanDays_ShouldReturnValidationError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/cleanup?OlderThanDays=400");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<int>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task DeleteOldReportsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().DeleteAsync($"{_baseUrl}/cleanup?OlderThanDays=30");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Helper Methods

    private async Task SeedMetricReportsAsync(int count, string metricType = "WorkerThroughput")
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        for (int i = 0; i < count; i++)
        {
            var report = new MetricReport
            {
                Id = Guid.CreateVersion7(),
                MetricType = metricType,
                DisplayName = $"Report {i + 1}",
                Description = $"Test report description {i + 1}",
                Data = $"{{\"value\": {i + 1}}}",
                PeriodStartTime = DateTime.UtcNow.AddDays(-7),
                PeriodEndTime = DateTime.UtcNow,
                GeneratedAt = DateTime.UtcNow.AddMinutes(-i),
                Tags = "test,integration",
                CreationDate = DateTime.UtcNow
            };

            await dbContext.MetricReports.AddAsync(report);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<MetricReport> SeedSingleMetricReportAsync(string metricType, string displayName, DateTime? generatedAt = null)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var report = new MetricReport
        {
            Id = Guid.CreateVersion7(),
            MetricType = metricType,
            DisplayName = displayName,
            Description = $"Test report for {metricType}",
            Data = "{\"value\": 42}",
            PeriodStartTime = DateTime.UtcNow.AddDays(-7),
            PeriodEndTime = DateTime.UtcNow,
            GeneratedAt = generatedAt ?? DateTime.UtcNow,
            Tags = "test",
            CreationDate = DateTime.UtcNow
        };

        await dbContext.MetricReports.AddAsync(report);
        await dbContext.SaveChangesAsync();

        return report;
    }

    #endregion
}
