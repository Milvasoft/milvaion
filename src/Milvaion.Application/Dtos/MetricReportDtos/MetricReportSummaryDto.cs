using System.Linq.Expressions;

namespace Milvaion.Application.Dtos.MetricReportDtos;

/// <summary>
/// Metric report metadata without the report payload.
/// </summary>
/// <remarks>
/// <see cref="MetricReportListDto"/> carries the full jsonb <c>Data</c> for every row it returns, which is fine for
/// a dashboard that renders a chart per card and expensive for anything that only needs to know which reports exist.
/// A page of twenty throughput reports is a few hundred kilobytes of aggregated series, none of it read if the
/// caller is deciding which report to open.
/// <para>
/// This shape answers "what is there and how fresh is it" and leaves fetching the payload to the detail query.
/// <see cref="DataSizeBytes"/> is kept so the size of that follow-up call is known before it is made.
/// </para>
/// </remarks>
public class MetricReportSummaryDto
{
    /// <summary> Unique identifier (Guid v7). </summary>
    public Guid Id { get; set; }

    /// <summary> Report type key (e.g. FailureRateTrend, WorkerThroughput, JobHealthScore). </summary>
    public string MetricType { get; set; }

    /// <summary> Human-readable report title. </summary>
    public string DisplayName { get; set; }

    /// <summary> Short description of what the report measures. </summary>
    public string Description { get; set; }

    /// <summary> Start of the analysis window (UTC). </summary>
    public DateTime PeriodStartTime { get; set; }

    /// <summary> End of the analysis window (UTC). </summary>
    public DateTime PeriodEndTime { get; set; }

    /// <summary> Timestamp when the report was generated (UTC). </summary>
    public DateTime GeneratedAt { get; set; }

    /// <summary> Comma-separated tags for categorization and filtering. </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Size of the omitted report payload in characters.
    /// </summary>
    public int DataSizeBytes { get; set; }

    /// <summary>
    /// How long ago the report was generated, in minutes.
    /// </summary>
    /// <remarks>
    /// Populated after materialisation rather than in the projection: the age is relative to now, and pushing
    /// <c>DateTime.UtcNow</c> into the SQL translation is both fragile and pointless when the row is about to be
    /// read in memory anyway.
    /// <para>
    /// It exists because a stale report is the single most common way to reach a confident wrong conclusion here.
    /// A reader given only <see cref="GeneratedAt"/> has to know the current time to spot that the failure rate
    /// trend it is quoting was computed six hours ago.
    /// </para>
    /// </remarks>
    public double AgeMinutes { get; set; }

    /// <summary>
    /// EF Core projection expression that maps a <see cref="MetricReport"/> entity to this DTO.
    /// </summary>
    public static Expression<Func<MetricReport, MetricReportSummaryDto>> Projection { get; } = entity => new MetricReportSummaryDto
    {
        Id = entity.Id,
        MetricType = entity.MetricType,
        DisplayName = entity.DisplayName,
        Description = entity.Description,
        PeriodStartTime = entity.PeriodStartTime,
        PeriodEndTime = entity.PeriodEndTime,
        GeneratedAt = entity.GeneratedAt,
        Tags = entity.Tags,
        DataSizeBytes = entity.Data == null ? 0 : entity.Data.Length
    };
}
