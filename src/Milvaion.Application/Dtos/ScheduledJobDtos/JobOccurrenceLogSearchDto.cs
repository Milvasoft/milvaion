namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// A page of execution log lines, plus what it took to get there.
/// </summary>
public class JobOccurrenceLogSearchDto
{
    /// <summary> The matching lines, newest first. </summary>
    public List<JobOccurrenceLogListDto> Logs { get; set; } = [];

    /// <summary>
    /// How many lines matched in total, or null when counting was skipped.
    /// </summary>
    /// <remarks>
    /// Counting an unfiltered log table means scanning it. The count is produced only when
    /// the search is bounded enough for it to be cheap; otherwise this is null and
    /// <see cref="HasMore"/> answers the question that actually matters - whether to keep
    /// paging.
    /// </remarks>
    public int? TotalCount { get; set; }

    /// <summary> Current page number. </summary>
    public int PageNumber { get; set; }

    /// <summary> Whether more lines match beyond this page. </summary>
    public bool HasMore { get; set; }

    /// <summary>
    /// Whether the values of structured fields were included.
    /// </summary>
    public bool DataIncluded { get; set; }
}
