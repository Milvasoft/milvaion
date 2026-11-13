using MediatR;
using Milvaion.Application.Features.ExclusionCalendars.CreateExclusionCalendar;
using Milvaion.Application.Features.ExclusionCalendars.DeleteExclusionCalendar;
using Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarDetail;
using Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarList;
using Milvaion.Application.Features.ExclusionCalendars.UpdateExclusionCalendar;
using Milvaion.Domain.JsonModels;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Milvaion.Api.Mcp;

/// <summary>
/// MCP tools covering exclusion calendars: named sets of days on which the jobs that reference them do not run.
/// </summary>
/// <remarks>
/// Like the job tools, every tool sends the same MediatR request the REST API uses.
/// </remarks>
[McpServerToolType]
public class MilvaionCalendarTools(IMediator mediator, McpPermissionGuard guard)
{
    private readonly IMediator _mediator = mediator;
    private readonly McpPermissionGuard _guard = guard;

    /// <summary>
    /// Lists exclusion calendars.
    /// </summary>
    /// <param name="searchTerm">Free text search over calendar names.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The calendars with their entry counts.</returns>
    [McpServerTool(Name = "list_exclusion_calendars", ReadOnly = true)]
    [Description("Lists exclusion calendars - named sets of dates, date ranges and weekdays on which the jobs that reference them are skipped. Use this to find a calendar id for create_job or update_job.")]
    public async Task<object> ListExclusionCalendarsAsync(
        [Description("Optional free text search over calendar names.")] string searchTerm = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ExclusionCalendarManagement.List);

        var response = await _mediator.Send(new GetExclusionCalendarListQuery
        {
            SearchTerm = searchTerm,
            PageNumber = 1,
            RowCount = 100
        }, cancellationToken);

        return new { totalCount = response.TotalDataCount, calendars = response.Data };
    }

    /// <summary>
    /// Gets one exclusion calendar.
    /// </summary>
    /// <param name="calendarId">Calendar id.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>The calendar and the jobs that use it.</returns>
    /// <exception cref="McpException">Thrown when the calendar does not exist.</exception>
    [McpServerTool(Name = "get_exclusion_calendar", ReadOnly = true)]
    [Description("Gets one exclusion calendar in full - its dates, ranges and weekdays - and the jobs that use it.")]
    public async Task<object> GetExclusionCalendarAsync(
        [Description("The calendar's GUID id, from list_exclusion_calendars.")] Guid calendarId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ExclusionCalendarManagement.Detail);

        var response = await _mediator.Send(new GetExclusionCalendarDetailQuery { CalendarId = calendarId }, cancellationToken);

        return response.Data ?? throw new McpException($"No exclusion calendar found with id {calendarId}.");
    }

    /// <summary>
    /// Creates an exclusion calendar.
    /// </summary>
    /// <param name="name">Unique name.</param>
    /// <param name="description">Description.</param>
    /// <param name="dates">Single excluded dates.</param>
    /// <param name="dateRanges">Excluded ranges.</param>
    /// <param name="weekdays">Excluded weekdays.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the new calendar.</returns>
    /// <exception cref="McpException">Thrown when the calendar could not be created.</exception>
    [McpServerTool(Name = "create_exclusion_calendar")]
    [Description("Creates an exclusion calendar. Dates are plain calendar dates (yyyy-MM-dd); each job compares them with the local date of its run in its own time zone. A calendar does nothing until a job references it through create_job or update_job.")]
    public async Task<object> CreateExclusionCalendarAsync(
        [Description("Unique name, e.g. 'Public holidays TR 2027'.")] string name,
        [Description("What the calendar is for.")] string description = null,
        [Description("Single excluded dates, e.g. ['2027-01-01', '2027-04-23'].")] List<DateOnly> dates = null,
        [Description("Excluded ranges, both ends inclusive, each { start, end, label }.")] List<ExclusionDateRange> dateRanges = null,
        [Description("Excluded weekdays, 0 = Sunday ... 6 = Saturday. [0, 6] excludes weekends.")] List<int> weekdays = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ExclusionCalendarManagement.Create);

        var response = await _mediator.Send(new CreateExclusionCalendarCommand
        {
            Name = name,
            Description = description,
            Dates = dates ?? [],
            DateRanges = dateRanges ?? [],
            Weekdays = weekdays ?? []
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to create the calendar.");

        return new { calendarId = response.Data, message = "Calendar created. Reference it from a job with update_job to take effect." };
    }

    /// <summary>
    /// Replaces an exclusion calendar.
    /// </summary>
    /// <param name="calendarId">Calendar id.</param>
    /// <param name="name">Unique name.</param>
    /// <param name="description">Description.</param>
    /// <param name="dates">Single excluded dates.</param>
    /// <param name="dateRanges">Excluded ranges.</param>
    /// <param name="weekdays">Excluded weekdays.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the calendar.</returns>
    /// <exception cref="McpException">Thrown when the calendar could not be updated.</exception>
    [McpServerTool(Name = "update_exclusion_calendar", Idempotent = true)]
    [Description("Replaces an exclusion calendar. Every list is written as given, so read it with get_exclusion_calendar first and send the full result - an omitted list empties it. The change affects every job that uses the calendar from its next run on; confirm with the user first.")]
    public async Task<object> UpdateExclusionCalendarAsync(
        [Description("The calendar's GUID id.")] Guid calendarId,
        [Description("Unique name.")] string name,
        [Description("What the calendar is for.")] string description = null,
        [Description("All single excluded dates.")] List<DateOnly> dates = null,
        [Description("All excluded ranges, each { start, end, label }.")] List<ExclusionDateRange> dateRanges = null,
        [Description("All excluded weekdays, 0 = Sunday ... 6 = Saturday.")] List<int> weekdays = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ExclusionCalendarManagement.Update);

        var response = await _mediator.Send(new UpdateExclusionCalendarCommand
        {
            Id = calendarId,
            Name = name,
            Description = description,
            Dates = dates ?? [],
            DateRanges = dateRanges ?? [],
            Weekdays = weekdays ?? []
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the calendar.");

        return new { calendarId = response.Data, message = "Calendar updated." };
    }

    /// <summary>
    /// Deletes an exclusion calendar.
    /// </summary>
    /// <param name="calendarId">Calendar id.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the deleted calendar.</returns>
    /// <exception cref="McpException">Thrown when the calendar could not be deleted.</exception>
    [McpServerTool(Name = "delete_exclusion_calendar", Destructive = true)]
    [Description("Deletes an exclusion calendar. Refused while any job still references it - remove it from those jobs with update_job first. Confirm with the user before calling.")]
    public async Task<object> DeleteExclusionCalendarAsync(
        [Description("The calendar's GUID id.")] Guid calendarId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ExclusionCalendarManagement.Delete);

        var response = await _mediator.Send(new DeleteExclusionCalendarCommand { CalendarId = calendarId }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to delete the calendar.");

        return new { calendarId = response.Data, message = "Calendar deleted." };
    }
}
