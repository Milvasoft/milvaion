using Milvasoft.Milvaion.Sdk.Domain.Enums;
using Milvasoft.Milvaion.Sdk.Domain.JsonModels;

namespace Milvasoft.Milvaion.Sdk.Models;

/// <summary>
/// Result of a job execution with all metrics and logs.
/// </summary>
public record JobExecutionResult
{
    public Guid CorrelationId { get; set; }
    public Guid JobId { get; set; }
    public string WorkerId { get; set; }
    public JobOccurrenceStatus Status { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public long DurationMs { get; set; }
    public string Result { get; set; }
    public string Exception { get; set; }
    public List<OccurrenceLog> Logs { get; set; }

    /// <summary>
    /// Indicates whether this failure is permanent and should not be retried.
    /// When true, the job will be sent directly to the Dead Letter Queue (DLQ).
    /// </summary>
    public bool IsPermanentFailure { get; set; }
}