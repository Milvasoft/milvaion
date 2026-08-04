using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvasoft.Milvaion.Sdk.Domain;

/// <summary>
/// Entity representing a generated metric report with statistical data.
/// </summary>
[Table(SchedulerTableNames.MetricReports)]
[DontIndexCreationDate]
public class MetricReport : CreationAuditableEntity<Guid>
{
    /// <summary>
    /// Type of the metric (e.g., FailureRate, P50P95P99, TopSlowJobs, WorkerThroughput, etc.)
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string MetricType { get; set; }

    /// <summary>
    /// Display name of the metric for UI
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string DisplayName { get; set; }

    /// <summary>
    /// Description of the metric
    /// </summary>
    [MaxLength(500)]
    public string Description { get; set; }

    /// <summary>
    /// JSON serialized metric data
    /// </summary>
    [Required]
    [Column(TypeName = "jsonb")]
    public string Data { get; set; }

    /// <summary>
    /// Size of the serialized <see cref="Data"/> payload in characters.
    /// </summary>
    /// <remarks>
    /// Persisted at write time so that summary/listing queries can report the payload size without transferring
    /// the jsonb column, and without computing <c>length()</c> over jsonb in SQL - PostgreSQL has no
    /// <c>length(jsonb)</c> function, so doing it in an EF projection breaks the query.
    /// </remarks>
    public int DataSizeBytes { get; set; }

    /// <summary>
    /// Start time of the data period (UTC)
    /// </summary>
    [Required]
    public DateTime PeriodStartTime { get; set; }

    /// <summary>
    /// End time of the data period (UTC)
    /// </summary>
    [Required]
    public DateTime PeriodEndTime { get; set; }

    /// <summary>
    /// Timestamp when the report was generated (UTC)
    /// </summary>
    [Required]
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tags for categorization and filtering (comma separated)
    /// </summary>
    [MaxLength(500)]
    public string Tags { get; set; }

    /// <summary>
    /// Period the report covers: Daily, Weekly, Monthly or Custom.
    /// </summary>
    /// <remarks>
    /// Lets the same metric type carry daily, weekly and monthly reports side by side and be filtered or labelled
    /// by period in the UI. <see cref="PeriodStartTime"/>/<see cref="PeriodEndTime"/> still hold the exact window.
    /// </remarks>
    [MaxLength(20)]
    public string Period { get; set; }
}
