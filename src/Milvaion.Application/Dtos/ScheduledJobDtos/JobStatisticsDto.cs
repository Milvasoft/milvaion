using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.ScheduledJobDtos;

/// <summary>
/// Data transfer object for scheduledjob list.
/// </summary>
[Translate]
public class JobStatisticsDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Total execution duration in milliseconds.
    /// Calculated as (EndTime - StartTime).
    /// </summary>
    public long? AvarageDuration { get; set; }

    /// <summary>
    /// Success rate percentage.
    /// </summary>
    public long? SuccessRate { get; set; }

    /// <summary>
    /// Total execution count.
    /// </summary>
    public long? TotalExecutions { get; set; }

    /// <summary>
    /// Projection expression for mapping ScheduledJob scheduledjob to ScheduledJobListDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<JobOccurrence, JobStatisticsDto>> Projection { get; } = r => new JobStatisticsDto
    {
        Id = r.Id,

    };
}
