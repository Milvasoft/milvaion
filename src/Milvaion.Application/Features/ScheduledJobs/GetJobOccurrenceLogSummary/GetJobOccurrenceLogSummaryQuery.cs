using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogSummary;

/// <summary>
/// Aggregates execution log lines over a time window.
/// </summary>
public record GetJobOccurrenceLogSummaryQuery : IQuery<JobOccurrenceLogSummaryDto>
{
    /// <summary> Only executions of this job. </summary>
    public Guid? JobId { get; set; }

    /// <summary> Only lines at this severity. </summary>
    public string Level { get; set; }

    /// <summary>
    /// Start of the window, UTC. Defaults to 24 hours before <see cref="Until"/>.
    /// </summary>
    public DateTime? Since { get; set; }

    /// <summary> End of the window, UTC. Defaults to now. </summary>
    public DateTime? Until { get; set; }

    /// <summary> Free text search over the message. </summary>
    public string SearchTerm { get; set; }

    /// <summary> How many entries to return in each breakdown. </summary>
    public int TopCount { get; set; } = 10;
}
