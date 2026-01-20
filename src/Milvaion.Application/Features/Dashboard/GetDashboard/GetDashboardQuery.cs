using Milvaion.Application.Dtos.DashboardDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Dashboard.GetDashboard;

/// <summary>
/// Query for getting dashboard statistics.
/// </summary>
public record GetDashboardQuery : IQuery<DashboardDto>
{
}