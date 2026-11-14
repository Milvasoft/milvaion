using Milvasoft.Attributes.Annotations;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.WorkflowDtos;

/// <summary>
/// Data transfer object for workflow list.
/// </summary>
[Translate]
public class WorkflowListDto : MilvaionBaseDto<Guid>
{
    /// <summary>
    /// Display name of the workflow.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of the workflow.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Tags for categorization.
    /// </summary>
    public string Tags { get; set; }

    /// <summary>
    /// Whether this workflow is active.
    /// </summary>
    public bool IsActive { get; set; }

    /// <summary>
    /// Failure handling strategy.
    /// </summary>
    public WorkflowFailureStrategy FailureStrategy { get; set; }

    /// <summary>
    /// Version of the workflow.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Number of steps in the workflow.
    /// </summary>
    public int StepCount { get; set; }

    /// <summary>
    /// Cron expression for automatic recurring execution.
    /// </summary>
    public string CronExpression { get; set; }

    /// <summary>
    /// Maximum reruns of a step at the time its job asked for (retry-after), per step and run.
    /// </summary>
    public int MaxRetryAfter { get; set; }

    /// <summary>
    /// Projection expression.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<Workflow, WorkflowListDto>> Projection { get; } = w => new WorkflowListDto
    {
        Id = w.Id,
        Name = w.Name,
        Description = w.Description,
        Tags = w.Tags,
        IsActive = w.IsActive,
        FailureStrategy = w.FailureStrategy,
        Version = w.Version,
        StepCount = w.Definition.Steps.Count,
        CronExpression = w.CronExpression,
        MaxRetryAfter = w.MaxRetryAfter,
    };
}
