using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.WorkflowDtos;

/// <summary>
/// Trigger workflow response containing the created WorkflowRun Id. The frontend can use this Id to track the workflow run status and details.
/// </summary>
public class TriggerWorkflowResponse : MilvaionBaseDto<Guid>, IHasActiviyLogDecision
{
    /// <summary>
    /// Time (UTC) the run is scheduled to start at; null when it starts right away.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    /// <summary>
    /// Determines whether this activity will be logged to database by the <see cref="UserActivityLogInterceptor"/> or not. Default is true, so the activity will be logged.
    /// </summary>
    [JsonIgnore]
    public bool ShouldLogActivity { get; set; } = true;
}
