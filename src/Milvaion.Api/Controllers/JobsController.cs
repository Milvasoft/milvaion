using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.FailedOccurrenceDtos;
using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvaion.Application.Features.FailedOccurrences.DeleteFailedOccurrence;
using Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceDetail;
using Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceList;
using Milvaion.Application.Features.FailedOccurrences.UpdateFailedOccurrence;
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
using Milvaion.Application.Utils.Attributes;
using Milvaion.Application.Utils.Constants;
using Milvaion.Application.Utils.PermissionManager;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// ScheduledJob endpoints.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager)]
public class JobsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets jobs.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.List)]
    [HttpPatch]
    public Task<ListResponse<ScheduledJobListDto>> GetScheduledJobsAsync(GetScheduledJobListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Get job according to job id.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Detail)]
    [HttpGet("job")]
    public Task<Response<ScheduledJobDetailDto>> GetScheduledJobAsync([FromQuery] GetScheduledJobDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Adds job.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Create)]
    [HttpPost("job")]
    public Task<Response<Guid>> AddScheduledJobAsync(CreateScheduledJobCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Updates job. Only the fields that are sent as isUpdated true are updated.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Update)]
    [HttpPut("job")]
    public Task<Response<Guid>> UpdateScheduledJobAsync(UpdateScheduledJobCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Removes job.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Delete)]
    [HttpDelete("job")]
    public Task<Response<Guid>> RemoveScheduledJobAsync([FromQuery] DeleteScheduledJobCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Manually triggers a scheduled job (creates occurrence and executes immediately).
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Trigger)]
    [HttpPost("job/trigger")]
    public Task<Response<Guid>> TriggerJobAsync(TriggerScheduledJobCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets job occurrences.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.List)]
    [HttpPatch("occurrences")]
    public Task<ListResponse<JobOccurrenceListDto>> GetJobOccurrencesAsync(GetJobOccurrenceListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets job occurrence detail.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Detail)]
    [HttpGet("occurrences/occurrence")]
    public Task<Response<JobOccurrenceDetailDto>> GetJobOccurrenceDetailAsync([FromQuery] GetJobOccurrenceDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Cancels a running job occurrence.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Cancel)]
    [HttpPost("occurrences/cancel")]
    public Task<Response<bool>> CancelJobOccurrenceAsync(CancelJobOccurrenceCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Deletes a job occurrence (only completed/failed/cancelled occurrences can be deleted).
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Delete)]
    [HttpDelete("occurrences/occurrence")]
    public Task<Response<List<Guid>>> DeleteJobOccurrenceAsync([FromBody] DeleteJobOccurrenceCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets job tags.
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.Detail)]
    [HttpGet("tags")]
    public Task<Response<List<string>>> GetTagsAsync(CancellationToken cancellation) => _mediator.Send(new GetTagListQuery(), cancellation);

    #region FailedOccurrences

    /// <summary>
    /// Gets failed occurrences.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.FailedOccurrenceManagement.List)]
    [HttpPatch("occurrences/failed")]
    public Task<ListResponse<FailedOccurrenceListDto>> GetFailedOccurrencesAsync(GetFailedOccurrenceListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Get failed occurrence according to failed occurrence id.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.FailedOccurrenceManagement.Detail)]
    [HttpGet("occurrences/occurrence/failed")]
    public Task<Response<FailedOccurrenceDetailDto>> GetFailedOccurrenceAsync([FromQuery] GetFailedOccurrenceDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Updates failed occurrence. Only the fields that are sent as isUpdated true are updated.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.FailedOccurrenceManagement.Update)]
    [HttpPut("occurrences/occurrence/failed")]
    public Task<Response<List<Guid>>> UpdateFailedOccurrenceAsync(UpdateFailedOccurrenceCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Removes failed occurrence.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.FailedOccurrenceManagement.Delete)]
    [HttpDelete("occurrences/occurrence/failed")]
    public Task<Response<List<Guid>>> RemoveFailedOccurrenceAsync([FromBody] DeleteFailedOccurrenceCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    #endregion
}