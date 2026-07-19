using Milvaion.Application.Dtos.UpcomingExecutionDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ScheduledJobs.GetUpcomingExecutionList;

/// <summary>
/// Asks for the runs that are coming up next.
/// </summary>
public record GetUpcomingExecutionListQuery : IQuery<UpcomingExecutionListDto>
{
    /// <summary>
    /// How far ahead to look, in hours. Defaults to a day.
    /// </summary>
    public int WithinHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of runs to return.
    /// </summary>
    public int Limit { get; set; } = 100;

    /// <summary>
    /// Filters by job or workflow name.
    /// </summary>
    public string SearchTerm { get; set; }

    /// <summary>
    /// Restricts the result to jobs or to workflows. Null returns both.
    /// </summary>
    public UpcomingExecutionKind? Kind { get; set; }

    /// <summary>
    /// Returns only entries that will not run - see <see cref="UpcomingExecutionHealth.NotScheduled"/>.
    /// </summary>
    public bool OnlyProblems { get; set; }
}
