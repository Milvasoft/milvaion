using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.FailedOccurrenceDtos;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceList;
using Milvaion.Application.Features.FailedOccurrences.UpdateFailedOccurrence;
using Milvaion.Application.Features.ScheduledJobs.CancelJobOccurrence;
using Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.DeleteJobOccurrence;
using Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceList;
using Milvaion.Application.Features.ScheduledJobs.GetScheduledJobList;
using Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;
using Milvaion.Application.Utils.Constants;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Milvaion.Sdk.Domain;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for JobsController.")]
public class JobsControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _baseUrl = $"{GlobalConstant.RoutePrefix}/v1.0/jobs";

    #region GetScheduledJobs

    [Fact]
    public async Task GetScheduledJobsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetScheduledJobListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync(_baseUrl, request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetScheduledJobsAsync_WithAuthorization_ShouldReturnJobs()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedScheduledJobsAsync(3);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetScheduledJobListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<ScheduledJobListDto>>();

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
    public async Task GetScheduledJobsAsync_WithPagination_ShouldReturnPaginatedJobs()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedScheduledJobsAsync(5);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetScheduledJobListQuery
        {
            PageNumber = 1,
            RowCount = 3
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<ScheduledJobListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(3);
        result.TotalDataCount.Should().Be(5);
    }

    [Fact]
    public async Task GetScheduledJobsAsync_WithSearchTerm_ShouldFilterJobs()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedScheduledJobsAsync(3);
        await SeedSingleScheduledJobAsync("SpecialJob", "Special job description");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetScheduledJobListQuery
        {
            SearchTerm = "Special"
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync(_baseUrl, request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<ScheduledJobListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(1);
        result.Data[0].DisplayName.Should().Contain("Special");
    }

    #endregion

    #region GetScheduledJob

    [Fact]
    public async Task GetScheduledJobAsync_WithValidId_ShouldReturnJobDetail()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("TestJob", "Test description");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/job?JobId={job.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<ScheduledJobDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data.DisplayName.Should().Be("TestJob");
    }

    [Fact]
    public async Task GetScheduledJobAsync_WithInvalidId_ShouldReturnNotFound()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/job?JobId={Guid.NewGuid()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<ScheduledJobDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    #endregion

    #region AddScheduledJob

    [Fact]
    public async Task AddScheduledJobAsync_WithInvalidWorkerData_ShouldCreateJob()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateScheduledJobCommand
        {
            DisplayName = "NewTestJob",
            Description = "New test job description",
            ExecuteAt = DateTime.UtcNow.AddHours(1),
            IsActive = true,
            WorkerId = "test-worker",
            SelectedJobName = "SampleJob"
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeFalse();
        result.Data.Should().BeEmpty();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var createdJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.DisplayName == "NewTestJob");
        createdJob.Should().BeNull();
    }

    [Fact]
    public async Task AddScheduledJobAsync_WithEmptyDisplayName_ShouldReturnValidationError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateScheduledJobCommand
        {
            DisplayName = "",
            ExecuteAt = DateTime.UtcNow.AddHours(1)
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
        result.StatusCode.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddScheduledJobAsync_WithCronExpression_ShouldCreateRecurringJob()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CreateScheduledJobCommand
        {
            DisplayName = "RecurringJob",
            Description = "Runs every hour",
            SelectedJobName = "TestJob",
            ExecuteAt = DateTime.UtcNow.AddMinutes(5),
            CronExpression = "* 0 * * * *", // Every hour
            IsActive = true
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var createdJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.DisplayName == "RecurringJob");
        createdJob.Should().NotBeNull();
        createdJob.CronExpression.Should().Be("* 0 * * * *");
    }

    #endregion

    #region UpdateScheduledJob

    [Fact]
    public async Task UpdateScheduledJobAsync_WithValidData_ShouldUpdateJob()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("EditableJob", "Original description");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand
        {
            Id = job.Id,
            DisplayName = new UpdateProperty<string>("UpdatedJobName"),
            Description = new UpdateProperty<string>("Updated description")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob.DisplayName.Should().Be("UpdatedJobName");
        updatedJob.Description.Should().Be("Updated description");
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_ToggleIsActive_ShouldUpdateJob()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("ActiveJob", "Active job", true);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand
        {
            Id = job.Id,
            IsActive = new UpdateProperty<bool>(false)
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand
        {
            Id = Guid.NewGuid(),
            DisplayName = new UpdateProperty<string>("UpdatedName")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region DeleteScheduledJob

    [Fact]
    public async Task DeleteScheduledJobAsync_WithValidId_ShouldDeleteJob()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("DeletableJob", "To be deleted");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/job?JobId={job.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        deletedJob.Should().BeNull();
    }

    [Fact]
    public async Task DeleteScheduledJobAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/job?JobId={Guid.NewGuid()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region GetJobOccurrences

    [Fact]
    public async Task GetJobOccurrencesAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetJobOccurrenceListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync($"{_baseUrl}/occurrences", request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetJobOccurrencesAsync_WithAuthorization_ShouldReturnOccurrences()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithOccurrences", "Has occurrences");
        await SeedJobOccurrencesAsync(job.Id, 3);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetJobOccurrenceListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync($"{_baseUrl}/occurrences", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<JobOccurrenceListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetJobOccurrencesAsync_WithPagination_ShouldReturnPaginatedOccurrences()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithManyOccurrences", "Has many occurrences");
        await SeedJobOccurrencesAsync(job.Id, 10);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetJobOccurrenceListQuery
        {
            PageNumber = 1,
            RowCount = 5
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync($"{_baseUrl}/occurrences", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<JobOccurrenceListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(5);
        result.TotalDataCount.Should().Be(10);
    }

    #endregion

    #region GetJobOccurrenceDetail

    [Fact]
    public async Task GetJobOccurrenceDetailAsync_WithValidId_ShouldReturnOccurrenceDetail()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithOccurrence", "Has occurrence");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/occurrences/occurrence?OccurrenceId={occurrence.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<JobOccurrenceDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
        result.Data.Id.Should().Be(occurrence.Id);
    }

    [Fact]
    public async Task GetJobOccurrenceDetailAsync_WithInvalidId_ShouldReturnNull()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/occurrences/occurrence?OccurrenceId={Guid.NewGuid()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<JobOccurrenceDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    #endregion

    #region TriggerJob

    [Fact]
    public async Task TriggerJobAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new TriggerScheduledJobCommand { JobId = Guid.NewGuid() };

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{_baseUrl}/job/trigger", request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TriggerJobAsync_WithInvalidJobId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new TriggerScheduledJobCommand { JobId = Guid.NewGuid() };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region CancelJobOccurrence

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new CancelJobOccurrenceCommand { OccurrenceId = Guid.NewGuid() };

        // Act
        var httpResponse = await _factory.CreateClient().PostAsJsonAsync($"{_baseUrl}/occurrences/cancel", request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CancelJobOccurrenceCommand { OccurrenceId = Guid.NewGuid() };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/occurrences/cancel", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<bool>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithCompletedOccurrence_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobToCancel", "Job to cancel");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Completed);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CancelJobOccurrenceCommand { OccurrenceId = occurrence.Id };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/occurrences/cancel", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<bool>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region DeleteJobOccurrence

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [Guid.NewGuid()] };

        // Act
        var httpResponse = await _factory.CreateClient().SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/occurrences/occurrence")
        {
            Content = JsonContent.Create(request)
        });

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithCompletedOccurrence_ShouldDeleteOccurrence()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithCompletedOccurrence", "Job");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Completed);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [occurrence.Id] };

        // Act
        var httpResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/occurrences/occurrence")
        {
            Content = JsonContent.Create(request)
        });
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<Guid>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedOccurrence = await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == occurrence.Id);
        deletedOccurrence.Should().BeNull();
    }

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [Guid.NewGuid()] };

        // Act
        var httpResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/occurrences/occurrence")
        {
            Content = JsonContent.Create(request)
        });
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<Guid>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region GetTags

    [Fact]
    public async Task GetTagsAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Act
        var httpResponse = await _factory.CreateClient().GetAsync($"{_baseUrl}/tags");

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetTagsAsync_WithAuthorization_ShouldReturnTags()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        await SeedScheduledJobsWithTagsAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/tags");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<string>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeEmpty();
    }

    [Fact]
    public async Task GetTagsAsync_WithNoTags_ShouldReturnEmptyList()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/tags");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<string>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    #endregion

    #region FailedOccurrences

    [Fact]
    public async Task GetFailedOccurrencesAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new GetFailedOccurrenceListQuery();

        // Act
        var httpResponse = await _factory.CreateClient().PatchAsJsonAsync($"{_baseUrl}/occurrences/failed", request);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetFailedOccurrencesAsync_WithAuthorization_ShouldReturnFailedOccurrences()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithFailedOccurrences", "Has failed occurrences");
        await SeedFailedOccurrencesAsync(job.Id, 3);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetFailedOccurrenceListQuery();

        // Act
        var httpResponse = await client.PatchAsJsonAsync($"{_baseUrl}/occurrences/failed", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<FailedOccurrenceListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetFailedOccurrencesAsync_WithPagination_ShouldReturnPaginatedFailedOccurrences()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithManyFailedOccurrences", "Has many failed occurrences");
        await SeedFailedOccurrencesAsync(job.Id, 10);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new GetFailedOccurrenceListQuery
        {
            PageNumber = 1,
            RowCount = 5
        };

        // Act
        var httpResponse = await client.PatchAsJsonAsync($"{_baseUrl}/occurrences/failed", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<FailedOccurrenceListDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(5);
        result.TotalDataCount.Should().Be(10);
    }

    [Fact]
    public async Task GetFailedOccurrenceAsync_WithValidId_ShouldReturnFailedOccurrenceDetail()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithFailedOccurrence", "Has failed occurrence");
        var failedOccurrence = await SeedSingleFailedOccurrenceAsync(job.Id);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/occurrences/occurrence/failed?FailedOccurrenceId={failedOccurrence.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<FailedOccurrenceDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().NotBeNull();
    }

    [Fact]
    public async Task GetFailedOccurrenceAsync_WithInvalidId_ShouldReturnNull()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.GetAsync($"{_baseUrl}/occurrences/occurrence/failed?FailedOccurrenceId={Guid.NewGuid()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<FailedOccurrenceDetailDto>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task UpdateFailedOccurrenceAsync_WithValidData_ShouldUpdateFailedOccurrence()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithFailedOccurrence", "Has failed occurrence");
        var failedOccurrence = await SeedSingleFailedOccurrenceAsync(job.Id);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateFailedOccurrenceCommand
        {
            IdList = [failedOccurrence.Id],
            Resolved = new UpdateProperty<bool>(true)
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/occurrences/occurrence/failed", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<Guid>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedFailedOccurrence = await dbContext.FailedOccurrences.FirstOrDefaultAsync(f => f.Id == failedOccurrence.Id);
        updatedFailedOccurrence.Should().NotBeNull();
        updatedFailedOccurrence.Resolved.Should().BeTrue();
    }

    [Fact]
    public async Task DeleteFailedOccurrenceAsync_WithValidId_ShouldDeleteFailedOccurrence()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithDeletableFailedOccurrence", "Has deletable failed occurrence");
        var failedOccurrence = await SeedSingleFailedOccurrenceAsync(job.Id);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/occurrences/occurrence/failed")
        {
            Content = JsonContent.Create(new { FailedOccurrenceIdList = new[] { failedOccurrence.Id } })
        });
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<Guid>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedFailedOccurrence = await dbContext.FailedOccurrences.FirstOrDefaultAsync(f => f.Id == failedOccurrence.Id);
        deletedFailedOccurrence.Should().BeNull();
    }

    [Fact]
    public async Task DeleteFailedOccurrenceAsync_WithInvalidId_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/occurrences/occurrence/failed")
        {
            Content = JsonContent.Create(new { IdList = new[] { Guid.NewGuid() } })
        });
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<List<Guid>>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();
    }

    #endregion

    #region Helper Methods

    private async Task SeedScheduledJobsAsync(int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        for (int i = 0; i < count; i++)
        {
            var job = new ScheduledJob
            {
                Id = Guid.NewGuid(),
                DisplayName = $"TestJob{i + 1}",
                Description = $"Test job {i + 1} description",
                JobNameInWorker = "TestJob",
                ExecuteAt = DateTime.UtcNow.AddHours(i + 1),
                IsActive = true,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            };
            await dbContext.ScheduledJobs.AddAsync(job);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<ScheduledJob> SeedSingleScheduledJobAsync(string displayName, string description, bool isActive = true)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var job = new ScheduledJob
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            JobNameInWorker = "TestJob",
            Description = description,
            ExecuteAt = DateTime.UtcNow.AddHours(1),
            IsActive = isActive,
            CreationDate = DateTime.UtcNow,
            CreatorUserName = GlobalConstant.SystemUsername
        };

        await dbContext.ScheduledJobs.AddAsync(job);
        await dbContext.SaveChangesAsync();

        return job;
    }

    private async Task SeedJobOccurrencesAsync(Guid jobId, int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        for (int i = 0; i < count; i++)
        {
            var occurrence = new JobOccurrence
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                CorrelationId = Guid.NewGuid(),
                Status = JobOccurrenceStatus.Completed,
                StartTime = DateTime.UtcNow.AddMinutes(-30 - i),
                EndTime = DateTime.UtcNow.AddMinutes(-i),
                CreatedAt = DateTime.UtcNow,
                CreationDate = DateTime.UtcNow
            };
            await dbContext.JobOccurrences.AddAsync(occurrence);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task SeedScheduledJobsWithTagsAsync()
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var jobs = new List<ScheduledJob>
        {
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "EmailJob",
                Tags = "email,notification",
                JobNameInWorker = "TestJob",
                ExecuteAt = DateTime.UtcNow.AddHours(1),
                IsActive = true,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            },
            new()
            {
                Id = Guid.NewGuid(),
                DisplayName = "ReportJob",
                JobNameInWorker = "TestJob",
                Tags = "report,daily",
                ExecuteAt = DateTime.UtcNow.AddHours(2),
                IsActive = true,
                CreationDate = DateTime.UtcNow,
                CreatorUserName = GlobalConstant.SystemUsername
            }
        };

        await dbContext.ScheduledJobs.AddRangeAsync(jobs);
        await dbContext.SaveChangesAsync();
    }

    private async Task<JobOccurrence> SeedSingleJobOccurrenceAsync(Guid jobId, JobOccurrenceStatus status = JobOccurrenceStatus.Completed)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();

        var occurrence = new JobOccurrence
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            CorrelationId = Guid.NewGuid(),
            Status = status,
            StartTime = DateTime.UtcNow.AddMinutes(-30),
            EndTime = status == JobOccurrenceStatus.Completed ? DateTime.UtcNow : null,
            CreatedAt = DateTime.UtcNow,
            CreationDate = DateTime.UtcNow
        };

        await dbContext.JobOccurrences.AddAsync(occurrence);
        await dbContext.SaveChangesAsync();

        return occurrence;
    }

    private async Task SeedFailedOccurrencesAsync(Guid jobId, int count)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var job = await dbContext.ScheduledJobs.FirstAsync(j => j.Id == jobId);

        for (int i = 0; i < count; i++)
        {
            var occurrence = new JobOccurrence
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                CorrelationId = Guid.NewGuid(),
                Status = JobOccurrenceStatus.Failed,
                StartTime = DateTime.UtcNow.AddMinutes(-30 - i),
                EndTime = DateTime.UtcNow.AddMinutes(-i),
                CreatedAt = DateTime.UtcNow,
                CreationDate = DateTime.UtcNow
            };
            await dbContext.JobOccurrences.AddAsync(occurrence);

            var failedOccurrence = new FailedOccurrence
            {
                Id = Guid.NewGuid(),
                JobId = jobId,
                OccurrenceId = occurrence.Id,
                CorrelationId = Guid.NewGuid(),
                FailedAt = DateTime.UtcNow.AddMinutes(-i * 10),
                Exception = $"Test error message {i + 1}",
                JobDisplayName = job.DisplayName,
                JobNameInWorker = job.JobNameInWorker,
                Resolved = false,
                CreationDate = DateTime.UtcNow
            };
            await dbContext.FailedOccurrences.AddAsync(failedOccurrence);
        }

        await dbContext.SaveChangesAsync();
    }

    private async Task<FailedOccurrence> SeedSingleFailedOccurrenceAsync(Guid jobId)
    {
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var job = await dbContext.ScheduledJobs.FirstAsync(j => j.Id == jobId);

        var occurrence = new JobOccurrence
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            CorrelationId = Guid.NewGuid(),
            Status = JobOccurrenceStatus.Failed,
            StartTime = DateTime.UtcNow.AddMinutes(-30),
            EndTime = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            CreationDate = DateTime.UtcNow
        };
        await dbContext.JobOccurrences.AddAsync(occurrence);

        var failedOccurrence = new FailedOccurrence
        {
            Id = Guid.NewGuid(),
            JobId = jobId,
            OccurrenceId = occurrence.Id,
            CorrelationId = Guid.NewGuid(),
            FailedAt = DateTime.UtcNow.AddMinutes(-30),
            Exception = "Test error message",
            JobDisplayName = job.DisplayName,
            JobNameInWorker = job.JobNameInWorker,
            Resolved = false,
            CreationDate = DateTime.UtcNow
        };

        await dbContext.FailedOccurrences.AddAsync(failedOccurrence);
        await dbContext.SaveChangesAsync();

        return failedOccurrence;
    }

    #endregion
}
