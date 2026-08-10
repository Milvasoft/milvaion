using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.DashboardDtos;
using Milvaion.Application.Features.Dashboard.GetDashboard;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Dashboard endpoints for retrieving application statistics.
/// </summary>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[Auth]
public class DashboardController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Get dashboard data.
    /// </summary>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ScheduledJobManagement.List)]
    [HttpGet]
    public Task<Response<DashboardDto>> GetRolesAsync(CancellationToken cancellation) => _mediator.Send(new GetDashboardQuery(), cancellation);
}