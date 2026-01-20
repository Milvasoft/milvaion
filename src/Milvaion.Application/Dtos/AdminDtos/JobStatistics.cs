namespace Milvaion.Application.Dtos.AdminDtos;

/// <summary>
/// Job statistics
/// </summary>
public record JobStatistics
{
    /// <summary>
    /// Total number of jobs
    /// </summary>
    public int TotalJobs { get; init; }

    /// <summary>
    /// Number of active jobs
    /// </summary>
    public int ActiveJobs { get; init; }

    /// <summary>
    /// Number of inactive jobs
    /// </summary>
    public int InactiveJobs { get; init; }

    /// <summary>
    /// Number of recurring jobs
    /// </summary>
    public int RecurringJobs { get; init; }

    /// <summary>
    /// Number of one-time jobs
    /// </summary>
    public int OneTimeJobs { get; init; }
}
