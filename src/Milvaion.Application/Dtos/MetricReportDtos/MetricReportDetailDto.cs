using System.Linq.Expressions;

namespace Milvaion.Application.Dtos.MetricReportDtos;

/// <summary>
/// Detailed DTO for a single metric report. Contains the full JSON report data along with metadata.
/// Used by detail and latest-report endpoints.
/// </summary>
public class MetricReportDetailDto
{
    /// <summary> Unique identifier (Guid v7). </summary>
    public Guid Id { get; set; }

    /// <summary> Report type key (e.g. FailureRateTrend, WorkerThroughput, JobHealthScore). </summary>
    public string MetricType { get; set; }

    /// <summary> Human-readable report title for UI display. </summary>
    public string DisplayName { get; set; }

    /// <summary> Short description of what the report measures. </summary>
    public string Description { get; set; }

    /// <summary> JSON-serialized report payload (stored as jsonb in PostgreSQL). </summary>
    public string Data { get; set; }

    /// <summary> Start of the analysis window (UTC). </summary>
    public DateTime PeriodStartTime { get; set; }

    /// <summary> End of the analysis window (UTC). </summary>
    public DateTime PeriodEndTime { get; set; }

    /// <summary> Timestamp when the report was generated (UTC). </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary> Comma-separated tags for categorization and filtering. </summary>
    public string Tags { get; set; }

    /// <summary> Record creation timestamp populated by the auditing infrastructure. </summary>
    public DateTime? CreationDate { get; set; }

    /// <summary> Username of the creator populated by the auditing infrastructure. </summary>
    public string CreatorUserName { get; set; }

    /// <summary>
    /// EF Core projection expression that maps a <see cref="MetricReport"/> entity to this DTO.
    /// </summary>
    public static Expression<Func<MetricReport, MetricReportDetailDto>> Projection { get; } = entity => new MetricReportDetailDto
    {
        Id = entity.Id,
        MetricType = entity.MetricType,
        DisplayName = entity.DisplayName,
        Description = entity.Description,
        Data = entity.Data,
        PeriodStartTime = entity.PeriodStartTime,
        PeriodEndTime = entity.PeriodEndTime,
        GeneratedAt = entity.GeneratedAt,
        Tags = entity.Tags,
        CreationDate = entity.CreationDate,
        CreatorUserName = entity.CreatorUserName
    };
}
