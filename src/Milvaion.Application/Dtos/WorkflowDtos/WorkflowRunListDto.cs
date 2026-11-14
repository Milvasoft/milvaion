using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.WorkflowDtos;

/// <summary>
/// Data transfer object for workflow run list.
/// </summary>
[Translate]
public class WorkflowRunListDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Parent workflow ID.
    /// </summary>
    public Guid WorkflowId { get; set; }

    /// <summary>
    /// Workflow version at execution time.
    /// </summary>
    public int WorkflowVersion { get; set; }

    /// <summary>
    /// Correlation ID.
    /// </summary>
    public Guid CorrelationId { get; set; }

    /// <summary>
    /// Status of the workflow run.
    /// </summary>
    public WorkflowStatus Status { get; set; }

    /// <summary>
    /// Start time.
    /// </summary>
    public DateTime? StartTime { get; set; }

    /// <summary>
    /// End time.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Duration in milliseconds.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Trigger reason.
    /// </summary>
    public string TriggerReason { get; set; }

    /// <summary>
    /// Time (UTC) a run triggered for later starts at; null for a run that started right away.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Created at.
    /// </summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Projection expression.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<WorkflowRun, WorkflowRunListDto>> Projection { get; } = r => new WorkflowRunListDto
    {
        Id = r.Id,
        WorkflowId = r.WorkflowId,
        WorkflowVersion = r.WorkflowVersion,
        CorrelationId = r.CorrelationId,
        Status = r.Status,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        DurationMs = r.DurationMs,
        TriggerReason = r.TriggerReason,
        ScheduledAt = r.ScheduledAt,
        CreatedAt = r.CreatedAt,
    };
}
