using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Milvaion.Application.Dtos.ExclusionCalendarDtos;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Dtos.UpcomingExecutionDtos;
using Milvaion.Application.Features.ExclusionCalendars.CreateExclusionCalendar;
using Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarList;
using Milvaion.Application.Features.ExclusionCalendars.UpdateExclusionCalendar;
using Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;
using Milvaion.Application.Interfaces.Redis;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Application.Utils.Scheduling;
using Milvaion.Domain.JsonModels;
using Milvaion.Infrastructure.Persistence.Context;
using Milvaion.IntegrationTests.TestBase;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Types.Structs;
using System.Net;
using System.Net.Http.Json;
using Xunit.Abstractions;

namespace Milvaion.IntegrationTests.ControllersTests;

/// <summary>
/// API tests for per-job time zones, missed-run policy, priority and exclusion calendars.
/// </summary>
[Collection(nameof(MilvaionTestCollection))]
[Trait("Controller Integration Tests", "Integration tests for scheduling features.")]
public class SchedulingFeaturesControllerTests(CustomWebApplicationFactory factory, ITestOutputHelper output) : IntegrationTestBase(factory, output)
{
    private const string _jobsUrl = $"{GlobalConstant.RoutePrefix}/v1.0/jobs";
    private const string _calendarsUrl = $"{GlobalConstant.RoutePrefix}/v1.0/exclusioncalendars";

    private async Task<HttpClient> RootClientAsync()
    {
        await SeedRootUserAndSuperAdminRoleAsync();

        return await _factory.CreateClient().LoginAsync();
    }

    private static async Task<Guid> CreateCalendarAsync(HttpClient client, CreateExclusionCalendarCommand command)
    {
        var response = await client.PostAsJsonAsync($"{_calendarsUrl}/calendar", command);
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();

        result.IsSuccess.Should().BeTrue(result.Messages?.FirstOrDefault()?.Message);

        return result.Data;
    }

    private static Task<Response<Guid>> CreateJobAsync(HttpClient client, CreateScheduledJobCommand command)
        => client.PostAsJsonAsync($"{_jobsUrl}/job", command).ContinueWith(t => t.Result.Content.ReadFromJsonAsync<Response<Guid>>()).Unwrap();

    #region Exclusion calendars

    [Fact]
    public async Task Calendars_Crud_ShouldCreateListUpdateAndDelete()
    {
        var client = await RootClientAsync();

        var id = await CreateCalendarAsync(client, new CreateExclusionCalendarCommand
        {
            Name = "Public holidays",
            Description = "National holidays",
            Dates = [new DateOnly(2027, 1, 1), new DateOnly(2027, 4, 23)],
            DateRanges = [new ExclusionDateRange { Start = new DateOnly(2027, 12, 24), End = new DateOnly(2027, 12, 26), Label = "Christmas" }]
        });

        // List
        var listResponse = await client.PatchAsJsonAsync(_calendarsUrl, new GetExclusionCalendarListQuery { SearchTerm = "holiday" });
        var list = await listResponse.Content.ReadFromJsonAsync<ListResponse<ExclusionCalendarListDto>>();

        list.IsSuccess.Should().BeTrue();
        list.Data.Should().ContainSingle(c => c.Id == id);
        list.Data[0].DateCount.Should().Be(2);
        list.Data[0].DateRangeCount.Should().Be(1);

        // Update replaces every field.
        var updateResponse = await client.PutAsJsonAsync($"{_calendarsUrl}/calendar", new UpdateExclusionCalendarCommand
        {
            Id = id,
            Name = "Holidays and weekends",
            Weekdays = [0, 6]
        });
        (await updateResponse.Content.ReadFromJsonAsync<Response<Guid>>()).IsSuccess.Should().BeTrue();

        // Detail
        var detail = await client.GetFromJsonAsync<Response<ExclusionCalendarDetailDto>>($"{_calendarsUrl}/calendar?CalendarId={id}");

        detail.Data.Name.Should().Be("Holidays and weekends");
        detail.Data.Dates.Should().BeEmpty();
        detail.Data.DateRanges.Should().BeEmpty();
        detail.Data.Weekdays.Should().Equal(0, 6);

        // Delete
        var deleteResponse = await client.DeleteAsync($"{_calendarsUrl}/calendar?CalendarId={id}");
        (await deleteResponse.Content.ReadFromJsonAsync<Response<Guid>>()).IsSuccess.Should().BeTrue();

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        (await dbContext.ExclusionCalendars.AnyAsync(c => c.Id == id)).Should().BeFalse();
    }

    [Fact]
    public async Task Calendars_Create_ShouldRejectDuplicateNameAndInvalidInput()
    {
        var client = await RootClientAsync();

        await CreateCalendarAsync(client, new CreateExclusionCalendarCommand { Name = "Weekends", Weekdays = [0, 6] });

        var duplicate = await (await client.PostAsJsonAsync($"{_calendarsUrl}/calendar", new CreateExclusionCalendarCommand { Name = "weekends", Weekdays = [0] }))
                                    .Content.ReadFromJsonAsync<Response<Guid>>();

        duplicate.IsSuccess.Should().BeFalse();
        duplicate.Messages.Should().Contain(m => m.Message.Contains("already exists"));

        var invalid = await client.PostAsJsonAsync($"{_calendarsUrl}/calendar", new CreateExclusionCalendarCommand { Name = "Bad", Weekdays = [9] });
        var invalidResult = await invalid.Content.ReadFromJsonAsync<Response<Guid>>();

        invalidResult.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Calendars_Delete_ShouldBeRefused_WhileAJobUsesIt()
    {
        var client = await RootClientAsync();
        var calendarId = await CreateCalendarAsync(client, new CreateExclusionCalendarCommand { Name = "In use", Weekdays = [3] });

        var job = await CreateJobAsync(client, new CreateScheduledJobCommand
        {
            DisplayName = "Calendar user",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 12 * * *",
            ExclusionCalendarIds = [calendarId]
        });
        job.IsSuccess.Should().BeTrue();

        var response = await client.DeleteAsync($"{_calendarsUrl}/calendar?CalendarId={calendarId}");
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();

        result.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("'Calendar user'"));

        var detail = await client.GetFromJsonAsync<Response<ExclusionCalendarDetailDto>>($"{_calendarsUrl}/calendar?CalendarId={calendarId}");
        detail.Data.Jobs.Should().ContainSingle(j => j.Id == job.Data);
    }

    [Fact]
    public async Task Calendars_ShouldRequirePermission()
    {
        await SeedUserWithPermissionsAsync("calendarreader", "defaultpass", PermissionCatalog.ExclusionCalendarManagement.List);
        var client = await _factory.CreateClient().LoginAsync("calendarreader");

        var list = await client.PatchAsJsonAsync(_calendarsUrl, new GetExclusionCalendarListQuery());
        list.StatusCode.Should().Be(HttpStatusCode.OK);

        var create = await client.PostAsJsonAsync($"{_calendarsUrl}/calendar", new CreateExclusionCalendarCommand { Name = "Nope", Weekdays = [1] });
        create.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anonymous = await _factory.CreateClient().PatchAsJsonAsync(_calendarsUrl, new GetExclusionCalendarListQuery());
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    #endregion

    #region Job fields

    [Fact]
    public async Task CreateJob_ShouldStoreSchedulingFields_AndScheduleInJobZone()
    {
        var client = await RootClientAsync();
        var calendarId = await CreateCalendarAsync(client, new CreateExclusionCalendarCommand { Name = "Never on the 30th of Feb", Dates = [new DateOnly(2030, 1, 1)] });

        var created = await CreateJobAsync(client, new CreateScheduledJobCommand
        {
            DisplayName = "Istanbul morning job",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 9 * * *",
            TimeZoneId = "Europe/Istanbul",
            MissedRunPolicy = MissedRunPolicy.RunAll,
            MaxCatchUpRuns = 5,
            Priority = 7,
            ExclusionCalendarIds = [calendarId]
        });

        created.IsSuccess.Should().BeTrue(created.Messages?.FirstOrDefault()?.Message);

        var detail = await client.GetFromJsonAsync<Response<ScheduledJobDetailDto>>($"{_jobsUrl}/job?JobId={created.Data}");

        detail.Data.TimeZoneId.Should().Be("Europe/Istanbul");
        detail.Data.MissedRunPolicy.Should().Be(MissedRunPolicy.RunAll);
        detail.Data.MaxCatchUpRuns.Should().Be(5);
        detail.Data.Priority.Should().Be(7);
        detail.Data.ExclusionCalendars.Should().ContainSingle(c => c.Id == calendarId && c.Name == "Never on the 30th of Feb");

        // 09:00 in Istanbul is 06:00 UTC all year round.
        var scheduledAt = await _serviceProvider.GetRequiredService<IRedisSchedulerService>().GetScheduledTimeAsync(created.Data);
        scheduledAt.Should().NotBeNull();
        scheduledAt.Value.Hour.Should().Be(6);
        scheduledAt.Value.Minute.Should().Be(0);
    }

    [Fact]
    public async Task CreateJob_ShouldDefaultToUtcSkipAndPriorityZero()
    {
        var client = await RootClientAsync();

        var created = await CreateJobAsync(client, new CreateScheduledJobCommand { DisplayName = "Defaults", SelectedJobName = "TestJob", CronExpression = "0 0 * * * *" });

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var job = await dbContext.ScheduledJobs.AsNoTracking().FirstAsync(j => j.Id == created.Data);

        job.TimeZoneId.Should().Be("UTC");
        job.MissedRunPolicy.Should().Be(MissedRunPolicy.Skip);
        job.MaxCatchUpRuns.Should().Be(10);
        job.Priority.Should().Be(0);
        job.ExclusionCalendarIds.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Turkey Standard Time", 0, 10)]
    [InlineData("Mars/Olympus_Mons", 0, 10)]
    [InlineData("UTC", 10, 10)]
    [InlineData("UTC", -1, 10)]
    [InlineData("UTC", 0, 0)]
    [InlineData("UTC", 0, 101)]
    public async Task CreateJob_ShouldRejectInvalidSchedulingFields(string timeZoneId, int priority, int maxCatchUpRuns)
    {
        var client = await RootClientAsync();

        var result = await CreateJobAsync(client, new CreateScheduledJobCommand
        {
            DisplayName = "Invalid",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 * * * *",
            TimeZoneId = timeZoneId,
            Priority = priority,
            MaxCatchUpRuns = maxCatchUpRuns
        });

        result.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task CreateJob_ShouldRejectUnknownCalendar()
    {
        var client = await RootClientAsync();

        var result = await CreateJobAsync(client, new CreateScheduledJobCommand
        {
            DisplayName = "Unknown calendar",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 * * * *",
            ExclusionCalendarIds = [Guid.CreateVersion7()]
        });

        result.IsSuccess.Should().BeFalse();
        result.Messages.Should().Contain(m => m.Message.Contains("Exclusion calendar not found"));
    }

    [Fact]
    public async Task UpdateJob_ShouldChangeZoneAndMoveNextRun()
    {
        var client = await RootClientAsync();
        var created = await CreateJobAsync(client, new CreateScheduledJobCommand { DisplayName = "Movable", SelectedJobName = "TestJob", CronExpression = "0 0 9 * * *" });

        var response = await client.PutAsJsonAsync($"{_jobsUrl}/job", new UpdateScheduledJobCommand
        {
            Id = created.Data,
            TimeZoneId = new UpdateProperty<string>("Asia/Tokyo"),
            Priority = new UpdateProperty<int>(3),
            MissedRunPolicy = new UpdateProperty<MissedRunPolicy>(MissedRunPolicy.RunOnce)
        });
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();

        result.IsSuccess.Should().BeTrue(result.Messages?.FirstOrDefault()?.Message);

        var dbContext = _serviceProvider.GetRequiredService<MilvaionDbContext>();
        var job = await dbContext.ScheduledJobs.AsNoTracking().FirstAsync(j => j.Id == created.Data);

        job.TimeZoneId.Should().Be("Asia/Tokyo");
        job.Priority.Should().Be(3);
        job.MissedRunPolicy.Should().Be(MissedRunPolicy.RunOnce);

        // 09:00 in Tokyo (UTC+9, no DST) is 00:00 UTC.
        var scheduledAt = await _serviceProvider.GetRequiredService<IRedisSchedulerService>().GetScheduledTimeAsync(created.Data);
        scheduledAt.Value.Hour.Should().Be(0);

        var cached = await _serviceProvider.GetRequiredService<IRedisSchedulerService>().GetCachedJobAsync(created.Data);
        cached.TimeZoneId.Should().Be("Asia/Tokyo");
        cached.Priority.Should().Be(3);
    }

    [Fact]
    public async Task UpdateJob_ShouldRejectInvalidZone()
    {
        var client = await RootClientAsync();
        var created = await CreateJobAsync(client, new CreateScheduledJobCommand { DisplayName = "Zone check", SelectedJobName = "TestJob", CronExpression = "0 0 9 * * *" });

        var response = await client.PutAsJsonAsync($"{_jobsUrl}/job", new UpdateScheduledJobCommand { Id = created.Data, TimeZoneId = new UpdateProperty<string>("Nowhere/Land") });
        var result = await response.Content.ReadFromJsonAsync<Response<Guid>>();

        result.IsSuccess.Should().BeFalse();
    }

    #endregion

    #region Upcoming executions

    [Fact]
    public async Task UpcomingExecutions_ShouldShowZoneAndLeaveOutExcludedDays()
    {
        var client = await RootClientAsync();

        // Daily at 12:00 UTC; exclude the date of the next run.
        var next = ZonedCron.GetNextOccurrence("0 0 12 * * *", DateTime.UtcNow, "UTC").Value;
        var calendarId = await CreateCalendarAsync(client, new CreateExclusionCalendarCommand { Name = "Skip next", Dates = [DateOnly.FromDateTime(next)] });

        var created = await CreateJobAsync(client, new CreateScheduledJobCommand
        {
            DisplayName = "Noon job",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 12 * * *",
            ExclusionCalendarIds = [calendarId]
        });

        var plain = await CreateJobAsync(client, new CreateScheduledJobCommand
        {
            DisplayName = "Tokyo job",
            SelectedJobName = "TestJob",
            CronExpression = "0 0 9 * * *",
            TimeZoneId = "Asia/Tokyo"
        });

        var upcoming = await client.GetFromJsonAsync<Response<UpcomingExecutionListDto>>($"{_jobsUrl}/upcoming?WithinHours=72&Kind=Job");

        var noon = upcoming.Data.Items.Single(i => i.Id == created.Data);
        noon.ScheduledAt.Should().Be(next.AddDays(1));
        noon.ShiftedByCalendar.Should().BeTrue();
        noon.TimeZoneId.Should().Be("UTC");

        var tokyo = upcoming.Data.Items.Single(i => i.Id == plain.Data);
        tokyo.TimeZoneId.Should().Be("Asia/Tokyo");
        tokyo.LocalScheduledAt.Value.Hour.Should().Be(9);
        tokyo.ShiftedByCalendar.Should().BeFalse();
    }

    #endregion
}
