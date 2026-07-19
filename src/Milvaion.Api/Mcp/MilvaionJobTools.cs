using MediatR;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Features.ScheduledJobs.CancelJobOccurrence;
using Milvaion.Application.Features.ScheduledJobs.CreateScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.DeleteJobOccurrence;
using Milvaion.Application.Features.ScheduledJobs.DeleteScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceDetail;
using Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceList;
using Milvaion.Application.Features.ScheduledJobs.GetScheduledJobDetail;
using Milvaion.Application.Features.ScheduledJobs.GetScheduledJobList;
using Milvaion.Application.Features.ScheduledJobs.GetTagList;
using Milvaion.Application.Features.ScheduledJobs.TriggerScheduledJob;
using Milvaion.Application.Features.ScheduledJobs.UpdateScheduledJob;
using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Types.Structs;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Milvaion.Api.Mcp;

/// <summary>
/// MCP tools covering scheduled jobs and their executions.
/// </summary>
/// <remarks>
/// Every tool delegates to the same MediatR queries the REST API uses. Nothing here reimplements data access, so
/// a change to filtering or projection shows up identically in the dashboard, the REST API and MCP.
/// </remarks>
[McpServerToolType]
public class MilvaionJobTools(IMediator mediator, McpPermissionGuard guard)
{
    private readonly IMediator _mediator = mediator;
    private readonly McpPermissionGuard _guard = guard;

    private const int _maxPageSize = 100;

    /// <summary>
    /// Builds a filter for an inclusive date range on <paramref name="dateField"/>, or null when neither bound
    /// is supplied.
    /// </summary>
    /// <remarks>
    /// Two criteria rather than a single Between, so that supplying only one end of the range still works -
    /// "since yesterday" is a far more common request than a closed interval.
    /// <para>
    /// Generic over the value type because the columns are not consistent: occurrences use <c>DateTime</c> while
    /// the activity log uses <c>DateTimeOffset</c>, and handing the filter the wrong one compares mismatched
    /// types.
    /// </para>
    /// </remarks>
    internal static List<FilterCriteria> BuildDateRangeCriterias<TDate>(string dateField, TDate? since, TDate? until) where TDate : struct
    {
        var criterias = new List<FilterCriteria>();

        if (since is not null)
            criterias.Add(new FilterCriteria { FilterBy = dateField, Value = since.Value, Type = FilterType.GreaterThanOrEqualTo });

        if (until is not null)
            criterias.Add(new FilterCriteria { FilterBy = dateField, Value = until.Value, Type = FilterType.LessThanOrEqualTo });

        return criterias;
    }

    /// <summary>
    /// Adds an equality criteria for <paramref name="value"/>, ignoring it when null or blank.
    /// </summary>
    internal static void AddEqualityCriteria(List<FilterCriteria> criterias, string field, object value)
    {
        if (value is null || (value is string s && string.IsNullOrWhiteSpace(s)))
            return;

        criterias.Add(new FilterCriteria { FilterBy = field, Value = value, Type = FilterType.EqualTo });
    }

    /// <summary>
    /// Wraps criterias in a request, or returns null when there is nothing to filter on.
    /// </summary>
    /// <remarks>
    /// Returning null rather than an empty request matters: an empty <c>Criterias</c> list is not the same thing
    /// as "no filtering" to every code path downstream.
    /// </remarks>
    internal static FilterRequest ToFilterRequest(List<FilterCriteria> criterias)
        => criterias is null || criterias.Count == 0 ? null : new FilterRequest { Criterias = criterias };

    /// <summary>
    /// Gets scheduled jobs.
    /// </summary>
    /// <param name="searchTerm">Free text search over job names and types.</param>
    /// <param name="tag">Only jobs carrying this tag.</param>
    /// <param name="workerId">Only jobs assigned to this worker.</param>
    /// <param name="isActive">Only active or only paused jobs.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged job list with the total count.</returns>
    [McpServerTool(Name = "list_jobs", ReadOnly = true)]
    [Description("Lists scheduled jobs in Milvaion. Use this to find a job's id before calling other tools, or to get an overview of what is scheduled. Narrow it down with workerId or isActive rather than paging through everything.")]
    public async Task<object> ListJobsAsync(
        [Description("Optional free text search over job names and types.")] string searchTerm = null,
        [Description("Only jobs carrying this tag, from list_tags.")] string tag = null,
        [Description("Only jobs assigned to this worker id, from list_workers.")] string workerId = null,
        [Description("True for only scheduled jobs, false for only paused ones. Omit for both.")] bool? isActive = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.List);

        var criterias = new List<FilterCriteria>();
        AddEqualityCriteria(criterias, nameof(ScheduledJob.WorkerId), workerId);
        AddEqualityCriteria(criterias, nameof(ScheduledJob.IsActive), isActive);

        // Tags are stored as one comma separated string, so this is a substring match rather than equality.
        // It will also match a tag that is a prefix of another - "billing" finds "billing-nightly" - which is
        // the more useful behaviour here anyway.
        if (!string.IsNullOrWhiteSpace(tag))
            criterias.Add(new FilterCriteria { FilterBy = nameof(ScheduledJob.Tags), Value = tag.Trim(), Type = FilterType.Contains });

        var response = await _mediator.Send(new GetScheduledJobListQuery
        {
            SearchTerm = searchTerm,
            Filtering = ToFilterRequest(criterias),
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize),
            Sorting = new SortRequest { SortBy = nameof(ScheduledJob.Id), Type = SortType.Desc }
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            jobs = response.Data
        };
    }

    /// <summary>
    /// Gets scheduled job according to job id.
    /// </summary>
    /// <param name="jobId">Job id to access details.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Job detail.</returns>
    /// <exception cref="McpException">Thrown when no job exists with the given id.</exception>
    [McpServerTool(Name = "get_job", ReadOnly = true)]
    [Description("Gets full detail for one scheduled job: schedule, worker, job data, retry and timeout settings, and current state.")]
    public async Task<object> GetJobAsync(
        [Description("The job's GUID id, as returned by list_jobs.")] Guid jobId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Detail);

        var response = await _mediator.Send(new GetScheduledJobDetailQuery { JobId = jobId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No job found with id {jobId}.");

        return response.Data;
    }

    /// <summary>
    /// Gets job occurrences.
    /// </summary>
    /// <param name="jobId">Only executions of this job.</param>
    /// <param name="status">Only executions in this status.</param>
    /// <param name="workerId">Only executions handled by this worker.</param>
    /// <param name="searchTerm">Free text search over job names.</param>
    /// <param name="since">Only executions created at or after this UTC time.</param>
    /// <param name="until">Only executions created at or before this UTC time.</param>
    /// <param name="pageNumber">Page number, starting at 1.</param>
    /// <param name="pageSize">Results per page, capped at <see cref="_maxPageSize"/>.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Paged occurrence list with the total count.</returns>
    [McpServerTool(Name = "list_occurrences", ReadOnly = true)]
    [Description("Lists job executions (occurrences) with their status, duration and result, newest first. Filter rather than page: jobId for one job's history, status to see only failures, since and until to bound the window. All times are UTC.")]
    public async Task<object> ListOccurrencesAsync(
        [Description("Only executions of this job, by GUID id from list_jobs.")] Guid? jobId = null,
        [Description("Only executions in this status.")] JobOccurrenceStatus? status = null,
        [Description("Only executions handled by this worker id.")] string workerId = null,
        [Description("Optional free text search over job names.")] string searchTerm = null,
        [Description("Only executions at or after this UTC time, e.g. 2026-07-18T22:00:00Z.")] DateTime? since = null,
        [Description("Only executions at or before this UTC time.")] DateTime? until = null,
        [Description("Page number, starting at 1.")] int pageNumber = 1,
        [Description("Results per page. Maximum 100.")] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.List);

        var criterias = BuildDateRangeCriterias(nameof(JobOccurrence.CreatedAt), since, until);
        AddEqualityCriteria(criterias, nameof(JobOccurrence.JobId), jobId);
        AddEqualityCriteria(criterias, nameof(JobOccurrence.Status), status);
        AddEqualityCriteria(criterias, nameof(JobOccurrence.WorkerId), workerId);

        var response = await _mediator.Send(new GetJobOccurrenceListQuery
        {
            SearchTerm = searchTerm,
            Filtering = ToFilterRequest(criterias),
            PageNumber = pageNumber < 1 ? 1 : pageNumber,
            RowCount = Math.Clamp(pageSize, 1, _maxPageSize),
            // Newest first, matching the dashboard. Without an explicit sort the database returns rows in
            // whatever order the plan happens to produce, so the model would see a different - and effectively
            // arbitrary - set from the one the user is looking at on screen.
            Sorting = new SortRequest { SortBy = nameof(JobOccurrence.CreatedAt), Type = SortType.Desc }
        }, cancellationToken);

        return new
        {
            totalCount = response.TotalDataCount,
            pageNumber,
            occurrences = response.Data
        };
    }

    /// <summary>
    /// Gets job occurrence according to occurrence id, including its logs and exception detail.
    /// </summary>
    /// <param name="occurrenceId">Occurrence id to access details.</param>
    /// <param name="logLines">How many of the most recent log lines to include.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Occurrence detail with the log tail.</returns>
    /// <exception cref="McpException">Thrown when no occurrence exists with the given id.</exception>
    [McpServerTool(Name = "get_occurrence", ReadOnly = true)]
    [Description("Gets one execution in full, including its exception detail and the tail of its log. This is the tool to reach for when diagnosing why a job failed. Logs are trimmed to the most recent lines by default because a chatty job can produce thousands; raise logLines if the answer is not in the tail.")]
    public async Task<object> GetOccurrenceAsync(
        [Description("The occurrence's GUID id, as returned by list_occurrences or list_failures.")] Guid occurrenceId,
        [Description("How many of the most recent log lines to return. Maximum 1000. Pass 0 for none.")] int logLines = 100,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Detail);

        var response = await _mediator.Send(new GetJobOccurrenceDetailQuery { OccurrenceId = occurrenceId }, cancellationToken);

        if (response.Data == null)
            throw new McpException($"No occurrence found with id {occurrenceId}.");

        var occurrence = response.Data;

        // A job that logs in a loop can produce tens of thousands of lines. Returning them all would not fail -
        // it would quietly consume the model's whole context on a single call, which is worse, because the
        // symptom is a vague, expensive answer rather than an error anyone can act on.
        var totalLogLines = occurrence.Logs?.Count ?? 0;
        var requestedLines = Math.Clamp(logLines, 0, 1000);

        var truncated = totalLogLines > requestedLines;

        if (occurrence.Logs is not null && truncated)
            occurrence.Logs = [.. occurrence.Logs.TakeLast(requestedLines)];

        return new
        {
            occurrence,
            logSummary = new
            {
                totalLogLines,
                returnedLogLines = occurrence.Logs?.Count ?? 0,
                truncated,
                // Said explicitly so the model knows more exists rather than concluding the log is complete.
                note = truncated
                    ? $"Showing the most recent {requestedLines} of {totalLogLines} log lines. Call again with a higher logLines if the cause is earlier in the run."
                    : null
            }
        };
    }

    /// <summary>
    /// Triggers a scheduled job immediately, outside its normal schedule.
    /// </summary>
    /// <remarks>
    /// Always dispatched with <c>Force</c> false, so the job's concurrency policy still applies.
    /// </remarks>
    /// <param name="jobId">Job id to trigger.</param>
    /// <param name="reason">Trigger reason, recorded in the execution history alongside the calling credential.</param>
    /// <param name="jobData">JSON payload for this run. Falls back to the job's configured data when empty.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the created occurrence.</returns>
    /// <exception cref="McpException">Thrown when the job could not be triggered.</exception>
    [McpServerTool(Name = "trigger_job")]
    [Description("Runs a scheduled job immediately, outside its normal schedule. This has real side effects in production - confirm with the user before calling it. Requires the job trigger permission.")]
    public async Task<object> TriggerJobAsync(
        [Description("The job's GUID id.")] Guid jobId,
        [Description("Why the job is being triggered. Recorded in the execution history.")] string reason = null,
        [Description("Optional JSON payload to pass to this run. Leave empty to use the job's configured data.")] string jobData = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Trigger);

        var response = await _mediator.Send(new TriggerScheduledJobCommand
        {
            JobId = jobId,
            // Attributing the trigger matters: someone reading the history later needs to know a machine did it.
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"Triggered via MCP by {_guard.CallerName}"
                : $"{reason} (via MCP by {_guard.CallerName})",
            JobData = jobData,
            // Never force. Concurrency policies exist for a reason and an agent is not the right thing to
            // override them - a human can do that from the dashboard.
            Force = false
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to trigger the job.");

        return new
        {
            occurrenceId = response.Data,
            message = "Job triggered. Use get_occurrence with this id to follow its progress."
        };
    }

    /// <summary>
    /// Gets the distinct tags in use across scheduled jobs.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns>Tag list.</returns>
    [McpServerTool(Name = "list_tags", ReadOnly = true)]
    [Description("Lists every tag in use across scheduled jobs. Useful for discovering how jobs are grouped before searching for them.")]
    public async Task<object> ListTagsAsync(CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.List);

        var response = await _mediator.Send(new GetTagListQuery(), cancellationToken);

        return new { tags = response.Data };
    }

    /// <summary>
    /// Enables or disables a scheduled job.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <c>update_job</c>. Pausing a misbehaving job is the most common intervention
    /// during an incident, and it should not require constructing a partial update payload.
    /// </remarks>
    /// <param name="jobId">Job id.</param>
    /// <param name="isActive">True to resume scheduling, false to pause it.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the updated job.</returns>
    /// <exception cref="McpException">Thrown when the job could not be updated.</exception>
    [McpServerTool(Name = "set_job_active", Idempotent = true)]
    [Description("Pauses or resumes a scheduled job. A paused job keeps its definition and history but is skipped by the dispatcher. This is the right tool for 'stop this job from running' - prefer it over deleting.")]
    public async Task<object> SetJobActiveAsync(
        [Description("The job's GUID id.")] Guid jobId,
        [Description("True to resume scheduling, false to pause.")] bool isActive,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Update);

        var response = await _mediator.Send(new UpdateScheduledJobCommand
        {
            Id = jobId,
            IsActive = new UpdateProperty<bool> { Value = isActive, IsUpdated = true }
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the job.");

        return new
        {
            jobId = response.Data,
            isActive,
            message = isActive ? "Job resumed." : "Job paused. It keeps its definition and history."
        };
    }

    /// <summary>
    /// Configures auto-disable for a scheduled job.
    /// </summary>
    /// <remarks>
    /// Separate from <c>update_job</c> for the same reason as <c>set_job_active</c>: these three values belong
    /// together, and asking for them as part of a general update makes a common request awkward to express.
    /// </remarks>
    /// <param name="jobId">Job id.</param>
    /// <param name="enabled">True to auto-disable after repeated failures, false to never auto-disable, null to fall back to the global setting.</param>
    /// <param name="threshold">Consecutive failures before the job is disabled. Null falls back to the global setting.</param>
    /// <param name="failureWindowMinutes">Window in which those failures must occur to count. Null falls back to the global setting.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the updated job and the settings applied.</returns>
    /// <exception cref="McpException">Thrown when the job could not be updated.</exception>
    [McpServerTool(Name = "set_job_auto_disable", Idempotent = true)]
    [Description("Configures whether a job disables itself after repeated failures, and on what threshold. Auto-disable stops a broken job from failing on schedule forever. Passing enabled false means this job is never auto-disabled no matter how often it fails; omitting a value falls back to the installation-wide setting. Threshold counts consecutive failures within the failure window, so old unrelated failures do not accumulate.")]
    public async Task<object> SetJobAutoDisableAsync(
        [Description("The job's GUID id.")] Guid jobId,
        [Description("True to auto-disable after repeated failures, false to never auto-disable. Omit to use the global setting.")] bool? enabled = null,
        [Description("How many consecutive failures trigger the disable. Omit to use the global setting.")] int? threshold = null,
        [Description("Window in minutes within which those failures must occur to count. Omit to use the global setting.")] int? failureWindowMinutes = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Update);

        var settings = new UpsertJobAutoDisableSettings
        {
            Enabled = enabled,
            Threshold = threshold,
            FailureWindowMinutes = failureWindowMinutes
        };

        var response = await _mediator.Send(new UpdateScheduledJobCommand
        {
            Id = jobId,
            AutoDisableSettings = new UpdateProperty<UpsertJobAutoDisableSettings> { Value = settings, IsUpdated = true }
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the job.");

        return new
        {
            jobId = response.Data,
            autoDisable = settings,
            message = enabled == false
                ? "Auto-disable turned off. This job will keep running on schedule however often it fails."
                : "Auto-disable settings applied."
        };
    }

    /// <summary>
    /// Creates a scheduled job.
    /// </summary>
    /// <param name="displayName">Display name of the job.</param>
    /// <param name="workerId">Worker that should execute the job.</param>
    /// <param name="selectedJobName">Job type on that worker, as reported by worker auto discovery.</param>
    /// <param name="cronExpression">Cron expression for a recurring job. Null makes it a one-off run at <paramref name="executeAt"/>.</param>
    /// <param name="executeAt">First or only execution time in UTC. Defaults to now when omitted.</param>
    /// <param name="jobData">JSON payload passed to the job.</param>
    /// <param name="description">Description of the job.</param>
    /// <param name="tags">Comma separated tags.</param>
    /// <param name="isActive">Whether the job starts out scheduled.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the created job.</returns>
    /// <exception cref="McpException">Thrown when the job could not be created.</exception>
    [McpServerTool(Name = "create_job")]
    [Description("Creates a scheduled job. Call list_workers first to find a valid workerId and the job types it can execute - a job pointed at a worker that cannot run it will never execute. Confirm the schedule with the user before creating.")]
    public async Task<object> CreateJobAsync(
        [Description("Display name of the job.")] string displayName,
        [Description("Worker id that should execute it, from list_workers.")] string workerId,
        [Description("Job type on that worker, from the worker's executable job types.")] string selectedJobName,
        [Description("Cron expression for a recurring job, e.g. '0 9 * * MON'. Omit for a one-off job.")] string cronExpression = null,
        [Description("First or only execution time, UTC. Defaults to now.")] DateTime? executeAt = null,
        [Description("JSON payload passed to the job at execution time.")] string jobData = null,
        [Description("Description of what the job does.")] string description = null,
        [Description("Comma separated tags.")] string tags = null,
        [Description("Whether the job is scheduled straight away. Pass false to create it paused.")] bool isActive = true,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Create);

        var response = await _mediator.Send(new CreateScheduledJobCommand
        {
            DisplayName = displayName,
            Description = description,
            Tags = tags,
            JobData = jobData,
            ExecuteAt = executeAt ?? DateTime.UtcNow,
            CronExpression = cronExpression,
            IsActive = isActive,
            WorkerId = workerId,
            SelectedJobName = selectedJobName
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to create the job.");

        return new
        {
            jobId = response.Data,
            message = "Job created. Use get_job with this id to verify the schedule."
        };
    }

    /// <summary>
    /// Updates a scheduled job. Only the arguments that are supplied are changed.
    /// </summary>
    /// <remarks>
    /// The underlying command uses <see cref="UpdateProperty{T}"/> to distinguish "not supplied" from "set to
    /// null". That distinction is expressed here as nullable arguments so the model never has to construct the
    /// wrapper shape: a null argument means unchanged.
    /// <para>
    /// The consequence is that a field cannot be cleared through this tool. Clearing is rare, and a model
    /// accidentally blanking a cron expression is a worse failure than not being able to blank one on purpose.
    /// </para>
    /// </remarks>
    /// <param name="jobId">Job id to update.</param>
    /// <param name="displayName">New display name, or null to leave unchanged.</param>
    /// <param name="description">New description, or null to leave unchanged.</param>
    /// <param name="tags">New comma separated tags, or null to leave unchanged.</param>
    /// <param name="cronExpression">New cron expression, or null to leave unchanged.</param>
    /// <param name="jobData">New JSON payload, or null to leave unchanged.</param>
    /// <param name="executionTimeoutSeconds">New execution timeout, or null to leave unchanged.</param>
    /// <param name="zombieTimeoutMinutes">New zombie timeout, or null to leave unchanged.</param>
    /// <param name="concurrentExecutionPolicy">New concurrency policy, or null to leave unchanged.</param>
    /// <param name="jobType">New job type on the worker, or null to leave unchanged.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the updated job.</returns>
    /// <exception cref="McpException">Thrown when the job could not be updated.</exception>
    [McpServerTool(Name = "update_job", Idempotent = true)]
    [Description("Updates a scheduled job. Only the arguments you supply are changed; omitted arguments are left alone and cannot be cleared. Changing a cron expression changes when production work runs - show the user the current and proposed schedule and confirm before calling. To pause a job use set_job_active, and for auto-disable settings use set_job_auto_disable.")]
    public async Task<object> UpdateJobAsync(
        [Description("The job's GUID id.")] Guid jobId,
        [Description("New display name. Omit to leave unchanged.")] string displayName = null,
        [Description("New description. Omit to leave unchanged.")] string description = null,
        [Description("New comma separated tags. Omit to leave unchanged.")] string tags = null,
        [Description("New cron expression. Omit to leave unchanged.")] string cronExpression = null,
        [Description("New JSON payload. Omit to leave unchanged.")] string jobData = null,
        [Description("New execution timeout in seconds, after which the worker cancels the run. Omit to leave unchanged.")] int? executionTimeoutSeconds = null,
        [Description("New zombie timeout in minutes, after which a job stuck in Queued is marked failed. Omit to leave unchanged.")] int? zombieTimeoutMinutes = null,
        [Description("What to do when the job is triggered while a previous run is still going. Omit to leave unchanged.")] ConcurrentExecutionPolicy? concurrentExecutionPolicy = null,
        [Description("New job type on the worker. Omit to leave unchanged - changing this repoints the job at different code.")] string jobType = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Update);

        var command = new UpdateScheduledJobCommand { Id = jobId };

        if (displayName is not null)
            command.DisplayName = new UpdateProperty<string> { Value = displayName, IsUpdated = true };

        if (description is not null)
            command.Description = new UpdateProperty<string> { Value = description, IsUpdated = true };

        if (tags is not null)
            command.Tags = new UpdateProperty<string> { Value = tags, IsUpdated = true };

        if (cronExpression is not null)
            command.CronExpression = new UpdateProperty<string> { Value = cronExpression, IsUpdated = true };

        if (jobData is not null)
            command.JobData = new UpdateProperty<string> { Value = jobData, IsUpdated = true };

        if (executionTimeoutSeconds is not null)
            command.ExecutionTimeoutSeconds = new UpdateProperty<int?> { Value = executionTimeoutSeconds, IsUpdated = true };

        if (zombieTimeoutMinutes is not null)
            command.ZombieTimeoutMinutes = new UpdateProperty<int?> { Value = zombieTimeoutMinutes, IsUpdated = true };

        if (concurrentExecutionPolicy is not null)
            command.ConcurrentExecutionPolicy = new UpdateProperty<ConcurrentExecutionPolicy> { Value = concurrentExecutionPolicy.Value, IsUpdated = true };

        if (jobType is not null)
            command.JobType = new UpdateProperty<string> { Value = jobType, IsUpdated = true };

        var response = await _mediator.Send(command, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to update the job.");

        return new
        {
            jobId = response.Data,
            message = "Job updated. Use get_job to confirm the result."
        };
    }

    /// <summary>
    /// Deletes a scheduled job along with its execution history.
    /// </summary>
    /// <param name="jobId">Job id to delete.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Id of the deleted job.</returns>
    /// <exception cref="McpException">Thrown when the job could not be deleted.</exception>
    [McpServerTool(Name = "delete_job", Destructive = true)]
    [Description("Permanently deletes a scheduled job and its history. This cannot be undone. If the intent is only to stop the job running, use set_job_active with false instead - that is almost always what the user actually wants. Always confirm explicitly before calling this.")]
    public async Task<object> DeleteJobAsync(
        [Description("The job's GUID id.")] Guid jobId,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Delete);

        var response = await _mediator.Send(new DeleteScheduledJobCommand { JobId = jobId }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to delete the job.");

        return new { jobId = response.Data, message = "Job deleted." };
    }

    /// <summary>
    /// Cancels a running job occurrence.
    /// </summary>
    /// <param name="occurrenceId">Occurrence id to cancel.</param>
    /// <param name="reason">Cancellation reason, recorded against the occurrence.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Whether the cancellation signal was published.</returns>
    /// <exception cref="McpException">Thrown when the occurrence could not be cancelled.</exception>
    [McpServerTool(Name = "cancel_occurrence")]
    [Description("Cancels a currently running execution. Use this for a job that is stuck or running far longer than it should. Cancellation is cooperative - the worker stops at its next cancellation point, so a job ignoring its cancellation token may keep running.")]
    public async Task<object> CancelOccurrenceAsync(
        [Description("The occurrence's GUID id.")] Guid occurrenceId,
        [Description("Why it is being cancelled.")] string reason = null,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Trigger);

        var response = await _mediator.Send(new CancelJobOccurrenceCommand
        {
            OccurrenceId = occurrenceId,
            Reason = string.IsNullOrWhiteSpace(reason)
                ? $"Cancelled via MCP by {_guard.CallerName}"
                : $"{reason} (via MCP by {_guard.CallerName})"
        }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to cancel the occurrence.");

        return new
        {
            cancelled = response.Data,
            message = "Cancellation signal sent. The worker stops at its next cancellation point."
        };
    }

    /// <summary>
    /// Deletes job occurrences.
    /// </summary>
    /// <param name="occurrenceIds">Occurrence ids to delete.</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Ids of the deleted occurrences.</returns>
    /// <exception cref="McpException">Thrown when the occurrences could not be deleted.</exception>
    [McpServerTool(Name = "delete_occurrences", Destructive = true)]
    [Description("Permanently deletes execution records. This is a bulk operation: pass every id in a single call rather than calling once per record. Removing occurrences removes audit history, so prefer leaving them alone and letting the maintenance worker's retention policy handle cleanup. Confirm explicitly before calling.")]
    public async Task<object> DeleteOccurrencesAsync(
        [Description("GUID ids of the occurrences to delete. Accepts many at once - send the whole set in one call.")] List<Guid> occurrenceIds,
        CancellationToken cancellationToken = default)
    {
        _guard.Require(PermissionCatalog.ScheduledJobManagement.Delete);

        if (occurrenceIds is null || occurrenceIds.Count == 0)
            throw new McpException("No occurrence ids supplied.");

        var response = await _mediator.Send(new DeleteJobOccurrenceCommand { OccurrenceIdList = occurrenceIds }, cancellationToken);

        if (!response.IsSuccess)
            throw new McpException(response.Messages?.FirstOrDefault()?.Message ?? "Failed to delete the occurrences.");

        return new { deletedIds = response.Data, deletedCount = response.Data?.Count ?? 0 };
    }
}
