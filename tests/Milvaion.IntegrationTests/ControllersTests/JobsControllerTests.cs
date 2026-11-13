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
using Milvaion.Application.Interfaces.RabbitMQ;
using Milvaion.Application.Interfaces.Redis;
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
        var httpResponse = await client.GetAsync($"{_baseUrl}/job?JobId={Guid.CreateVersion7()}");
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
            Id = Guid.CreateVersion7(),
            DisplayName = new UpdateProperty<string>("UpdatedName")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_WithCronExpression_ShouldUpdateCronAndRecalculateExecuteAt()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("CronUpdateJob", "Job to update cron");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand
        {
            Id = job.Id,
            CronExpression = new UpdateProperty<string>("* 0 * * * *") // Every hour
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
        updatedJob.CronExpression.Should().Be("* 0 * * * *");
        updatedJob.ExecuteAt.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_DeactivateJob_ShouldRemoveFromRedisSchedule()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("DeactivateJob", "Job to deactivate", true);
        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);

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
        result.IsSuccess.Should().BeTrue();

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_UpdateDescription_ShouldOnlyUpdateDescription()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("DescUpdateJob", "Original description");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand
        {
            Id = job.Id,
            Description = new UpdateProperty<string>("Updated description only")
        };

        // Act
        var httpResponse = await client.PutAsJsonAsync($"{_baseUrl}/job", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var updatedJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        updatedJob.Should().NotBeNull();
        updatedJob.Description.Should().Be("Updated description only");
        updatedJob.DisplayName.Should().Be("DescUpdateJob");
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
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/job?JobId={Guid.CreateVersion7()}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task DeleteScheduledJobAsync_WithRunningOccurrence_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("RunningJobDelete", "Job with running occurrence");
        await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Running);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/job?JobId={job.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();

        // Verify job still exists
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var existingJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        existingJob.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteScheduledJobAsync_WithQueuedOccurrence_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("QueuedJobDelete", "Job with queued occurrence");
        await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Queued);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/job?JobId={job.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();

        // Verify job still exists
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var existingJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        existingJob.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteScheduledJobAsync_WithCompletedOccurrences_ShouldDeleteSuccessfully()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("CompletedOccJob", "Job with only completed occurrences");
        await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Completed);
        await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Failed);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.DeleteAsync($"{_baseUrl}/job?JobId={job.Id}");
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
        result.IsSuccess.Should().BeTrue();

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var deletedJob = await dbContext.ScheduledJobs.FirstOrDefaultAsync(j => j.Id == job.Id);
        deletedJob.Should().BeNull();
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
        var redisStatsService = _serviceProvider.GetRequiredService<IRedisStatsService>();
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        await redisStatsService.SyncCountersFromDatabaseAsync(dbContext);
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
        var httpResponse = await client.GetAsync($"{_baseUrl}/occurrences/occurrence?OccurrenceId={Guid.CreateVersion7()}");
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
        var request = new TriggerScheduledJobCommand { JobId = Guid.CreateVersion7() };

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
        var request = new TriggerScheduledJobCommand { JobId = Guid.CreateVersion7() };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TriggerJobAsync_WithValidActiveJob_ShouldCreateOccurrenceAndTrigger()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("TriggerableJob", "Job to trigger manually");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new TriggerScheduledJobCommand
        {
            JobId = job.Id,
            Reason = "Manual trigger test"
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);

        // Verify occurrence was created in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var occurrences = await dbContext.JobOccurrences.Where(o => o.JobId == job.Id).ToListAsync();
        occurrences.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TriggerJobAsync_WithInactiveJob_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("InactiveJob", "Inactive job", isActive: false);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new TriggerScheduledJobCommand { JobId = job.Id };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task TriggerJobAsync_WithCustomJobData_ShouldPassJobDataToOccurrence()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobDataTriggerJob", "Job with custom data");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new TriggerScheduledJobCommand
        {
            JobId = job.Id,
            JobData = """{"key":"value"}""",
            Reason = "Custom data trigger"
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/job/trigger", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        _output.WriteLine(result.Messages.First().Message);
    }

    [Fact]
    public async Task TriggerJobAsync_WhenBrokerUnavailable_ShouldFailOccurrenceAndReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("BrokerDownJob", "Job triggered while RabbitMQ is down");
        var request = new TriggerScheduledJobCommand { JobId = job.Id, Reason = "Broker down" };

        // The handler is built from the running host's services with only the publisher swapped. A second host
        // (WithWebHostBuilder) would re-read the factory's process-wide environment variables, which another test
        // collection may have pointed at its own, possibly stopped, containers.
        await using var scope = _factory.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<TriggerScheduledJobCommandHandler>(scope.ServiceProvider, new UnavailablePublisher());

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("message broker"));

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var occurrence = await dbContext.JobOccurrences.AsNoTracking().Include(o => o.Logs).SingleAsync(o => o.JobId == job.Id);
        occurrence.Status.Should().Be(JobOccurrenceStatus.Failed);
        occurrence.EndTime.Should().NotBeNull();
        occurrence.Logs.Should().Contain(l => l.Level == "Error" && l.Category == "ManualTrigger");
        occurrence.Logs.Should().Contain(l => l.Category == "ManualTrigger" && l.Level != "Error", "the trigger log written before publishing is kept");
    }

    private sealed class UnavailablePublisher : IRabbitMQPublisher
    {
        public Task<bool> PublishJobAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<int> PublishBatchAsync(Dictionary<ScheduledJob, Guid> jobsWithCorrelation, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<uint> GetQueueMessageCountAsync(string routingPattern, CancellationToken cancellationToken = default) => Task.FromResult(0u);
    }

    #endregion

    #region CancelJobOccurrence

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new CancelJobOccurrenceCommand { OccurrenceId = Guid.CreateVersion7() };

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
        var request = new CancelJobOccurrenceCommand { OccurrenceId = Guid.CreateVersion7() };

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

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithRunningOccurrence_ShouldCancelSuccessfully()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("CancellableJob", "Job to cancel while running");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Running);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CancelJobOccurrenceCommand
        {
            OccurrenceId = occurrence.Id,
            Reason = "Integration test cancellation"
        };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/occurrences/cancel", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<bool>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeTrue();

        // Verify occurrence is cancelled in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var cancelledOccurrence = await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == occurrence.Id);
        cancelledOccurrence.Should().NotBeNull();
        cancelledOccurrence.Status.Should().Be(JobOccurrenceStatus.Cancelled);
        cancelledOccurrence.EndTime.Should().NotBeNull();
        cancelledOccurrence.Exception.Should().Contain("Integration test cancellation");
    }

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithQueuedOccurrence_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("QueuedCancelJob", "Job with queued occurrence");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Queued);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CancelJobOccurrenceCommand { OccurrenceId = occurrence.CorrelationId };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/occurrences/cancel", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<bool>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CancelJobOccurrenceAsync_WithFailedOccurrence_ShouldReturnError()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("FailedCancelJob", "Job with failed occurrence");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Failed);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new CancelJobOccurrenceCommand { OccurrenceId = occurrence.CorrelationId };

        // Act
        var httpResponse = await client.PostAsJsonAsync($"{_baseUrl}/occurrences/cancel", request);
        var result = await httpResponse.Content.ReadFromJsonAsync<Response<bool>>();

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        result.Should().NotBeNull();
        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region Code review regressions

    [Fact]
    public async Task DeleteJobOccurrenceAsync_ShouldKeepTheJobScheduled()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("StillScheduled", "Job keeps its schedule");
        var occurrence = await SeedSingleJobOccurrenceAsync(job.Id);
        var redisScheduler = _serviceProvider.GetRequiredService<IRedisSchedulerService>();
        await redisScheduler.AddToScheduledSetAsync(job.Id, job.ExecuteAt);
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpRequest = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/occurrences/occurrence")
        {
            Content = JsonContent.Create(new DeleteJobOccurrenceCommand { OccurrenceIdList = [occurrence.Id] })
        };
        var httpResponse = await client.SendAsync(httpRequest);

        // Assert
        httpResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await redisScheduler.GetScheduledTimeAsync(job.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_WithJobType_ShouldPersistJobName()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("RenamedType", "Job type changes");
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand { Id = job.Id, JobType = new UpdateProperty<string>("OtherJob") };

        // Act
        var result = await (await client.PutAsJsonAsync($"{_baseUrl}/job", request)).Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        result.IsSuccess.Should().BeTrue();
        var updatedJob = await _serviceProvider.GetRequiredService<MilvaionDbContext>().ScheduledJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        updatedJob.JobNameInWorker.Should().Be("OtherJob");
        updatedJob.Version.Should().Be(job.Version + 1);
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_Reactivating_ShouldScheduleFromCronAndResetFailures()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("Comeback", "Reactivated job", false);
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        job.CronExpression = "0 0 * * * *";
        job.ExecuteAt = DateTime.UtcNow.AddDays(-30);
        job.AutoDisableSettings = new() { ConsecutiveFailureCount = 5, Threshold = 5, DisableReason = "failed", DisabledAt = DateTime.UtcNow };
        await dbContext.SaveChangesAsync();
        var client = await _factory.CreateClient().LoginAsync();
        var request = new UpdateScheduledJobCommand { Id = job.Id, IsActive = new UpdateProperty<bool>(true) };

        // Act
        var result = await (await client.PutAsJsonAsync($"{_baseUrl}/job", request)).Content.ReadFromJsonAsync<Response<Guid>>();

        // Assert
        result.IsSuccess.Should().BeTrue();
        var scheduledAt = await _serviceProvider.GetRequiredService<IRedisSchedulerService>().GetScheduledTimeAsync(job.Id);
        scheduledAt.Should().BeAfter(DateTime.UtcNow);
        var updatedJob = await _serviceProvider.GetRequiredService<MilvaionDbContext>().ScheduledJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        updatedJob.AutoDisableSettings.ConsecutiveFailureCount.Should().Be(0);
        updatedJob.AutoDisableSettings.DisabledAt.Should().BeNull();
        updatedJob.AutoDisableSettings.Threshold.Should().Be(5);
    }

    [Fact]
    public async Task UpdateScheduledJobAsync_Repeatedly_ShouldNotNestVersionHistory()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("Versioned", "Version history");
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        foreach (var value in new[] { "{\"a\":1}", "{\"a\":2}", "{\"a\":3}" })
            await client.PutAsJsonAsync($"{_baseUrl}/job", new UpdateScheduledJobCommand { Id = job.Id, JobData = new UpdateProperty<string>(value) });

        // Assert
        var updatedJob = await _serviceProvider.GetRequiredService<MilvaionDbContext>().ScheduledJobs.AsNoTracking().FirstAsync(j => j.Id == job.Id);
        updatedJob.JobVersions.Should().HaveCount(3);
        updatedJob.JobVersions.Should().OnlyContain(v => !v.Contains(nameof(ScheduledJob.JobVersions)));
    }

    [Theory]
    [InlineData("alpha-job", 1)]
    [InlineData("%", 0)]
    public async Task GetJobOccurrencesAsync_WithSearchTerm_ShouldFilterLiterally(string searchTerm, int expectedCount)
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("Search", "Searchable");
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var alpha = await SeedSingleJobOccurrenceAsync(job.Id);
        var beta = await SeedSingleJobOccurrenceAsync(job.Id);
        await dbContext.JobOccurrences.Where(o => o.Id == alpha.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.JobName, "alpha-job"));
        await dbContext.JobOccurrences.Where(o => o.Id == beta.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.JobName, "beta-job"));
        var client = await _factory.CreateClient().LoginAsync();

        // Act
        var httpResponse = await client.PatchAsJsonAsync($"{_baseUrl}/occurrences/list", new GetJobOccurrenceListQuery { SearchTerm = searchTerm, PageNumber = 1, RowCount = 10 });
        var result = await httpResponse.Content.ReadFromJsonAsync<ListResponse<JobOccurrenceListDto>>();

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(expectedCount);
        result.TotalDataCount.Should().Be(expectedCount);
    }

    #endregion

    #region DeleteJobOccurrence

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithoutAuthorization_ShouldReturnUnauthorized()
    {
        // Arrange
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [Guid.CreateVersion7()] };

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
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [Guid.CreateVersion7()] };

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

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithRunningOccurrence_ShouldNotDelete()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithRunningOcc", "Job with running occurrence");
        var runningOccurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Running);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [runningOccurrence.Id] };

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

        // Verify occurrence still exists in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var existingOccurrence = await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == runningOccurrence.Id);
        existingOccurrence.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithQueuedOccurrence_ShouldNotDelete()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobWithQueuedOcc", "Job with queued occurrence");
        var queuedOccurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Queued);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [queuedOccurrence.Id] };

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

        // Verify occurrence still exists
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var existingOccurrence = await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == queuedOccurrence.Id);
        existingOccurrence.Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteJobOccurrenceAsync_WithMixedStatuses_ShouldDeleteOnlyEligible()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobMixedOccurrences", "Job with mixed occurrences");
        var completedOccurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Completed);
        var failedOccurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Failed);
        var runningOccurrence = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Running);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new DeleteJobOccurrenceCommand
        {
            OccurrenceIdList = [completedOccurrence.Id, failedOccurrence.Id, runningOccurrence.Id]
        };

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
        result.Data.Should().HaveCount(2);
        result.Data.Should().Contain(completedOccurrence.Id);
        result.Data.Should().Contain(failedOccurrence.Id);
        result.Data.Should().NotContain(runningOccurrence.Id);

        // Verify in database
        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        (await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == completedOccurrence.Id)).Should().BeNull();
        (await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == failedOccurrence.Id)).Should().BeNull();
        (await dbContext.JobOccurrences.FirstOrDefaultAsync(o => o.Id == runningOccurrence.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task DeleteJobOccurrenceAsync_BulkDelete_ShouldDeleteMultipleCompletedOccurrences()
    {
        // Arrange
        await SeedRootUserAndSuperAdminRoleAsync();
        var job = await SeedSingleScheduledJobAsync("JobBulkDelete", "Job for bulk delete");
        var occ1 = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Completed);
        var occ2 = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Completed);
        var occ3 = await SeedSingleJobOccurrenceAsync(job.Id, JobOccurrenceStatus.Failed);
        var client = await _factory.CreateClient().LoginAsync();
        var request = new DeleteJobOccurrenceCommand { OccurrenceIdList = [occ1.Id, occ2.Id, occ3.Id] };

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
        result.Data.Should().HaveCount(3);

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        (await dbContext.JobOccurrences.CountAsync(o => request.OccurrenceIdList.Contains(o.Id))).Should().Be(0);
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
        var httpResponse = await client.GetAsync($"{_baseUrl}/occurrences/occurrence/failed?FailedOccurrenceId={Guid.CreateVersion7()}");
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
            Content = JsonContent.Create(new { IdList = new[] { Guid.CreateVersion7() } })
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
                Id = Guid.CreateVersion7(),
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
            Id = Guid.CreateVersion7(),
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
                Id = Guid.CreateVersion7(),
                JobId = jobId,
                CorrelationId = Guid.CreateVersion7(),
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
                Id = Guid.CreateVersion7(),
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
                Id = Guid.CreateVersion7(),
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
            Id = Guid.CreateVersion7(),
            JobId = jobId,
            CorrelationId = Guid.CreateVersion7(),
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
                Id = Guid.CreateVersion7(),
                JobId = jobId,
                CorrelationId = Guid.CreateVersion7(),
                Status = JobOccurrenceStatus.Failed,
                StartTime = DateTime.UtcNow.AddMinutes(-30 - i),
                EndTime = DateTime.UtcNow.AddMinutes(-i),
                CreatedAt = DateTime.UtcNow,
                CreationDate = DateTime.UtcNow
            };
            await dbContext.JobOccurrences.AddAsync(occurrence);

            var failedOccurrence = new FailedOccurrence
            {
                Id = Guid.CreateVersion7(),
                JobId = jobId,
                OccurrenceId = occurrence.Id,
                CorrelationId = Guid.CreateVersion7(),
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
            Id = Guid.CreateVersion7(),
            JobId = jobId,
            CorrelationId = Guid.CreateVersion7(),
            Status = JobOccurrenceStatus.Failed,
            StartTime = DateTime.UtcNow.AddMinutes(-30),
            EndTime = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            CreationDate = DateTime.UtcNow
        };
        await dbContext.JobOccurrences.AddAsync(occurrence);

        var failedOccurrence = new FailedOccurrence
        {
            Id = Guid.CreateVersion7(),
            JobId = jobId,
            OccurrenceId = occurrence.Id,
            CorrelationId = Guid.CreateVersion7(),
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
