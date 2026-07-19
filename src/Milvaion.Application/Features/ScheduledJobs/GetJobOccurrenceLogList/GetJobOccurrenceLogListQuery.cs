using Milvaion.Application.Dtos.ScheduledJobDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurrenceLogList;

/// <summary>
/// Searches execution log lines across every job.
/// </summary>
public record GetJobOccurrenceLogListQuery : IQuery<JobOccurrenceLogSearchDto>
{
    /// <summary> Only lines from executions of this job. </summary>
    public Guid? JobId { get; set; }

    /// <summary> Only lines from this one execution. </summary>
    public Guid? OccurrenceId { get; set; }

    /// <summary> Only lines at this severity, e.g. Error. </summary>
    public string Level { get; set; }

    /// <summary> Only lines in this category. </summary>
    public string Category { get; set; }

    /// <summary> Only lines recording this exception type. </summary>
    public string ExceptionType { get; set; }

    /// <summary> Only lines at or after this UTC time. </summary>
    public DateTime? Since { get; set; }

    /// <summary> Only lines at or before this UTC time. </summary>
    public DateTime? Until { get; set; }

    /// <summary> Free text search over the message. </summary>
    public string SearchTerm { get; set; }

    /// <summary>
    /// Whether to return the values of the structured fields, not just their names.
    /// </summary>
    public bool IncludeData { get; set; }

    /// <summary> Page number, starting at 1. </summary>
    public int PageNumber { get; set; } = 1;

    /// <summary> Lines per page. </summary>
    public int RowCount { get; set; } = 50;
}
