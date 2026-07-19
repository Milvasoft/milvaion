using MediatR;
using Milvaion.Application.Features.ActivityLogs.GetActivityLogList;
using Milvaion.Application.Features.FailedOccurrences.DeleteFailedOccurrence;
using Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceDetail;
using Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceList;
using Milvaion.Application.Features.FailedOccurrences.UpdateFailedOccurrence;
using Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogList;
using Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogSummary;
using Milvasoft.Types.Structs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Milvaion.Api.Mcp;

/// <summary>
/// MCP tools for diagnosing failures and reading the audit trail.
/// </summary>
/// <remarks>
/// These are the tools an assistant reaches for during an incident: what failed, why, and what changed
/// beforehand. They delegate to the same MediatR queries the REST API uses.
/// </remarks>
[McpServerToolType]
public class MilvaionDiagnosticsTools(IMediator mediator, McpPermissionGuard guard)
{
    private readonly IMediator _mediator = mediator;
    private readonly McpPermissionGuard _guard = guard;

    private const int _maxPageSize = 100;

    /// <summary>
    /// Log lines are smaller than the other records, so a page can hold more of them.
    /// </summary>
    private const int _maxLogPageSize = 200;

    /// <summary>
    /// Gets failed occurrences that exhausted their retries.
    /// </summary>
    /// <param name="jobId">Only failures of this job.</param>
    /// <param name="resolved">Only resolved or only unresolved failures.</param>
    /// <param name="searchTerm">Free text search over job names.</param>
    /// <param name="since">Only failures at or after this UTC time.</param>
    /// <param name="until">Only failures at or before this UTC time.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged failed occurrence list with the total count.</returns>
    [McpServerTool(Name = "list_failures", ReadOnly = true)]
    [Description("Lists jobs that exhausted their retries and landed in the dead letter queue, newest first. Start here for questions like 'what broke last night' - it is a much smaller and more relevant set than all occurrences. Filter with jobId for one job's failure history, resolved false to see only what still needs attention, and since to bound the window. Times are UTC.")]
    public async Task<object> ListFailuresAsync(
        [Description("Only failures of this job, by GUID id from list_jobs.")] Guid? jobId = null,
        [Description("False for failures nobody has dealt with yet, true for ones already resolved. Omit for both.")] bool? resolved = null,
        [Description("Optional free text search over job names.")] string searchTerm = null,
        [Description("Only failures at or after this UTC time, e.g. 2026-07-18T22:00:00Z.")] DateTime? since = null,
        [Description("Only failures at or before this UTC time.")] DateTime? until = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.FailedOccurrenceManagement.List);

        var criterias = MilvaionJobTools.BuildDateRangeCriterias(nameof(FailedOccurrence.FailedAt), since, until);
        MilvaionJobTools.AddEqualityCriteria(criterias, nameof(FailedOccurrence.JobId), jobId);
        MilvaionJobTools.AddEqualityCriteria(criterias, nameof(FailedOccurrence.Resolved), resolved);

        var response = await _mediator.Send(new GetFailedOccurrenceListQuery
        {
            SearchTerm = searchTerm,
            Filtering = MilvaionJobTools.ToFilterRequest(criterias),
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize),
            // Newest first. Without an explicit sort the rows come back in whatever order the query plan
            // produces, which would not match the dashboard and would make "what broke last night" meaningless.
            Sorting = new SortRequest { SortBy = nameof(FailedOccurrence.Id), Type = SortType.Desc }
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            failures = response.Data
        };
    }

    /// <summary>
    /// Gets a failed occurrence in full.
    /// </summary>
    /// <param name="failedOccurrenceId">Failed occurrence id to access details.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Failed occurrence detail.</returns>
    /// <exception cref="McpException">Thrown when no failed occurrence exists with the given id.</exception>
    [McpServerTool(Name = "get_failure", ReadOnly = true)]
    [Description("Gets one dead lettered failure in full, including the exception, failure categorisation and any resolution notes already recorded. Use this after list_failures to understand a specific failure.")]
    public async Task<object> GetFailureAsync(
        [Description("The failed occurrence's GUID id, as returned by list_failures.")] Guid failedOccurrenceId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.FailedOccurrenceManagement.Detail);

        var response = await _mediator.Send(new GetFailedOccurrenceDetailQuery { FailedOccurrenceId = failedOccurrenceId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No failed occurrence found with id {failedOccurrenceId}.");

        return response.Data;
    }

    /// <summary>
    /// Marks failed occurrences as resolved, with notes describing what was done.
    /// </summary>
    /// <remarks>
    /// This records a human decision about a failure. It does not re-run anything - use <c>trigger_job</c> for
    /// that, then mark the failure resolved describing what was retried.
    /// </remarks>
    /// <param name="failedOccurrenceIds">Failed occurrence ids to mark resolved.</param>
    /// <param name="resolutionNote">Explanation of the root cause or decision.</param>
    /// <param name="resolutionAction">What was done, e.g. "Retried manually", "Ignored - invalid data".</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Ids of the updated failures.</returns>
    /// <exception cref="McpException">Thrown when the failures could not be updated.</exception>
    [McpServerTool(Name = "resolve_failures", Idempotent = true)]
    [Description("Marks dead lettered failures as resolved with a note and an action description. This is a bulk operation: when several failures share a cause, resolve them together in one call with the same note rather than one call each. It records that a failure was dealt with; it does not re-run anything. To actually re-run the work, call trigger_job and then record that here as the action taken.")]
    public async Task<object> ResolveFailuresAsync(
        [Description("GUID ids of the failures to mark resolved. Accepts many at once - send the whole set in one call.")] List<Guid> failedOccurrenceIds,
        [Description("Explanation of the root cause or the decision taken.")] string resolutionNote,
        [Description("What was done, e.g. 'Retried manually' or 'Ignored - invalid data'.")] string resolutionAction = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.FailedOccurrenceManagement.Update);

        if (failedOccurrenceIds is null || failedOccurrenceIds.Count == 0)
            throw new McpException("No failed occurrence ids supplied.");

        var response = await _mediator.Send(new UpdateFailedOccurrenceCommand
        {
            IdList = failedOccurrenceIds,
            Resolved = new UpdateProperty<bool> { Value = true, IsUpdated = true },
            ResolvedAt = new UpdateProperty<DateTime?> { Value = DateTime.UtcNow, IsUpdated = true },
            // Attribution again: a resolution note that does not say a machine wrote it is misleading.
            ResolvedBy = new UpdateProperty<string> { Value = $"MCP - {_guard.CallerName}", IsUpdated = true },
            ResolutionNote = new UpdateProperty<string> { Value = resolutionNote, IsUpdated = true },
            ResolutionAction = new UpdateProperty<string> { Value = resolutionAction, IsUpdated = resolutionAction is not null }
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the failures.");

        return new { resolvedIds = response.Data, resolvedCount = response.Data?.Count ?? 0 };
    }

    /// <summary>
    /// Deletes failed occurrences.
    /// </summary>
    /// <param name="failedOccurrenceIds">Failed occurrence ids to delete.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Ids of the deleted failures.</returns>
    /// <exception cref="McpException">Thrown when the failures could not be deleted.</exception>
    [McpServerTool(Name = "delete_failures", Destructive = true)]
    [Description("Permanently deletes dead letter records. This is a bulk operation: pass every id in a single call rather than calling once per record - deleting 200 failures is one call with 200 ids, not 200 calls. Prefer resolve_failures, which keeps the record and explains what happened; deleting loses the evidence that a failure ever occurred. Confirm explicitly before calling.")]
    public async Task<object> DeleteFailuresAsync(
        [Description("GUID ids of the failures to delete. Accepts many at once - send the whole set in one call.")] List<Guid> failedOccurrenceIds,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.FailedOccurrenceManagement.Delete);

        if (failedOccurrenceIds is null || failedOccurrenceIds.Count == 0)
            throw new McpException("No failed occurrence ids supplied.");

        var response = await _mediator.Send(new DeleteFailedOccurrenceCommand { FailedOccurrenceIdList = failedOccurrenceIds }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to delete the failures.");

        return new { deletedIds = response.Data, deletedCount = response.Data?.Count ?? 0 };
    }

    /// <summary>
    /// Gets the user activity log.
    /// </summary>
    /// <param name="since">Only activities at or after this UTC time.</param>
    /// <param name="until">Only activities at or before this UTC time.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged activity log with the total count.</returns>
    [McpServerTool(Name = "list_activity_logs", ReadOnly = true)]
    [Description("Lists who changed what in Milvaion - job edits, triggers, deletions and similar, newest first. Reach for this when a job's behaviour changed and nobody knows why: the answer is often a configuration change recorded here. Bound the search with since and until, in UTC, around the time the behaviour changed.")]
    public async Task<object> ListActivityLogsAsync(
        [Description("Only activities at or after this UTC time, e.g. 2026-07-18T22:00:00Z.")] DateTime? since = null,
        [Description("Only activities at or before this UTC time.")] DateTime? until = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ActivityLogManagement.List);

        var response = await _mediator.Send(new GetActivityLogListQuery
        {
            // ActivityDate is a DateTimeOffset, so the bounds are converted rather than passed straight through.
            // Explicit type argument: the conditional cannot infer a common type from a null literal and a
            // DateTimeOffset, and ActivityDate is a DateTimeOffset rather than a DateTime like the other columns.
            Filtering = MilvaionJobTools.ToFilterRequest(MilvaionJobTools.BuildDateRangeCriterias<DateTimeOffset>(
                nameof(ActivityLog.ActivityDate),
                since is null ? null : new DateTimeOffset(DateTime.SpecifyKind(since.Value, DateTimeKind.Utc)),
                until is null ? null : new DateTimeOffset(DateTime.SpecifyKind(until.Value, DateTimeKind.Utc)))),
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize)
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            activityLogs = response.Data
        };
    }

    /// <summary>
    /// Aggregates execution logs across every job.
    /// </summary>
    /// <param name="jobId">Only executions of this job.</param>
    /// <param name="level">Only lines at this severity.</param>
    /// <param name="since">Start of the window, UTC.</param>
    /// <param name="until">End of the window, UTC.</param>
    /// <param name="searchTerm">Free text search over the message.</param>
    /// <param name="topCount">Entries per breakdown.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Counts by level, category, exception type, job and message.</returns>
    [McpServerTool(Name = "summarize_logs", ReadOnly = true)]
    [Description("Aggregates worker execution logs into counts - by severity, category, exception type, job and repeated message - over a time window. Start here rather than with search_logs whenever the question is about the whole system: 'what is going wrong', 'which job is noisiest', 'what errors are new today'. The response size does not grow with the log volume, so this is safe to call on a busy installation where reading the lines themselves would not be. Defaults to the last 24 hours. Times are UTC.")]
    public async Task<object> SummarizeLogsAsync(
        [Description("Only executions of this job, by GUID id from list_jobs.")] Guid? jobId = null,
        [Description("Only lines at this severity, e.g. Error, Warning, Information.")] string level = null,
        [Description("Start of the window, UTC. Defaults to 24 hours before the end.")] DateTime? since = null,
        [Description("End of the window, UTC. Defaults to now.")] DateTime? until = null,
        [Description("Optional free text search over the message.")] string searchTerm = null,
        [Description("How many entries to return in each breakdown. Maximum 50.")] int topCount = 10,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Detail);

        var response = await _mediator.Send(new GetJobOccurrenceLogSummaryQuery
        {
            JobId = jobId,
            Level = level,
            Since = since,
            Until = until,
            SearchTerm = searchTerm,
            TopCount = Math.Clamp(topCount, 1, 50)
        }, cancellationToken);

        return response.Data;
    }

    /// <summary>
    /// Searches execution log lines.
    /// </summary>
    /// <param name="jobId">Only executions of this job.</param>
    /// <param name="occurrenceId">Only this one execution.</param>
    /// <param name="level">Only lines at this severity.</param>
    /// <param name="category">Only lines in this category.</param>
    /// <param name="exceptionType">Only lines recording this exception type.</param>
    /// <param name="searchTerm">Free text search over the message.</param>
    /// <param name="since">Only lines at or after this UTC time.</param>
    /// <param name="until">Only lines at or before this UTC time.</param>
    /// <param name="includeData">Whether to return the values of structured fields.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Lines per page.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Matching log lines, newest first.</returns>
    [McpServerTool(Name = "search_logs", ReadOnly = true)]
    [Description("Searches individual worker execution log lines, newest first. Use this once summarize_logs has told you what to look for - it returns raw lines, so a broad search burns through a lot of output for little insight. Narrow it: jobId or occurrenceId for one job or run, level Error for failures only, searchTerm for a phrase seen in a message. Always pair searchTerm with since: message search is a substring scan and the time bound is what keeps it fast. Each line lists the names of its structured fields; set includeData true to also get their values, but note those values are written by the worker and can contain business data, so ask for them only when the names suggest they will answer the question. Times are UTC.")]
    public async Task<object> SearchLogsAsync(
        [Description("Only executions of this job, by GUID id from list_jobs.")] Guid? jobId = null,
        [Description("Only this one execution, by GUID id from list_occurrences.")] Guid? occurrenceId = null,
        [Description("Only lines at this severity, e.g. Error, Warning, Information.")] string level = null,
        [Description("Only lines in this category, e.g. Dispatcher. Categories come back from summarize_logs.")] string category = null,
        [Description("Only lines recording this exception type, as reported by summarize_logs.")] string exceptionType = null,
        [Description("Free text search over the message text, case insensitive.")] string searchTerm = null,
        [Description("Only lines at or after this UTC time, e.g. 2026-07-18T22:00:00Z.")] DateTime? since = null,
        [Description("Only lines at or before this UTC time.")] DateTime? until = null,
        [Description("True to include the values of the structured fields, not just their names. Worker-controlled content - request it deliberately.")] bool includeData = false,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Lines per page. Maximum 200.")] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Detail);

        var response = await _mediator.Send(new GetJobOccurrenceLogListQuery
        {
            JobId = jobId,
            OccurrenceId = occurrenceId,
            Level = level,
            Category = category,
            ExceptionType = exceptionType,
            SearchTerm = searchTerm,
            Since = since,
            Until = until,
            IncludeData = includeData,
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxLogPageSize)
        }, cancellationToken);

        return response.Data;
    }
}
