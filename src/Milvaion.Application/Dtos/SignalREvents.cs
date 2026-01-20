namespace Milvaion.Application.Dtos;

/// <summary>
/// Event data for job occurrence created event.
/// </summary>
public record OccurrenceCreatedSignal
{
    /// <summary>
    /// Id of a job occurrence.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Id of a job.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Name of the job occurrence.
    /// </summary>
    public string JobName { get; set; }

    /// <summary>
    /// Create time of the job occurrence.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Start time of the job occurrence.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time of the job occurrence.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Worker id of the job occurrence.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Status of the job occurrence.
    /// </summary>
    public int Status { get; set; }
}

/// <summary>
/// Event data for job occurrence updated event.
/// </summary>
public record OccurrenceUpdatedSignal
{
    /// <summary>
    /// Id of a job occurrence.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Status of the job occurrence.
    /// </summary>
    public int Status { get; set; }

    /// <summary>
    /// Start time of the job occurrence.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time of the job occurrence.
    /// </summary>
    public DateTime? EndTime { get; set; }
}
