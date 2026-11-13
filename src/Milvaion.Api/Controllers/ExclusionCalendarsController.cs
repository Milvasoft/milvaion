using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.ExclusionCalendarDtos;
using Milvaion.Application.Features.ExclusionCalendars.CreateExclusionCalendar;
using Milvaion.Application.Features.ExclusionCalendars.DeleteExclusionCalendar;
using Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarDetail;
using Milvaion.Application.Features.ExclusionCalendars.GetExclusionCalendarList;
using Milvaion.Application.Features.ExclusionCalendars.UpdateExclusionCalendar;
using Milvaion.Application.Utils.Attributes;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Exclusion calendar endpoints. A calendar holds days on which the jobs that reference it do not run.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[Auth]
public class ExclusionCalendarsController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Lists exclusion calendars.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ExclusionCalendarManagement.List)]
    [HttpPatch]
    public Task<ListResponse<ExclusionCalendarListDto>> GetExclusionCalendarsAsync(GetExclusionCalendarListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets one exclusion calendar with the jobs that use it.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ExclusionCalendarManagement.Detail)]
    [HttpGet("calendar")]
    public Task<Response<ExclusionCalendarDetailDto>> GetExclusionCalendarAsync([FromQuery] GetExclusionCalendarDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Creates an exclusion calendar.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ExclusionCalendarManagement.Create)]
    [HttpPost("calendar")]
    public Task<Response<Guid>> AddExclusionCalendarAsync(CreateExclusionCalendarCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Replaces an exclusion calendar. Every field is written, so send the full calendar.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ExclusionCalendarManagement.Update)]
    [HttpPut("calendar")]
    public Task<Response<Guid>> UpdateExclusionCalendarAsync(UpdateExclusionCalendarCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Deletes an exclusion calendar. Refused while any job still references it.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ExclusionCalendarManagement.Delete)]
    [HttpDelete("calendar")]
    public Task<Response<Guid>> RemoveExclusionCalendarAsync([FromQuery] DeleteExclusionCalendarCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}
