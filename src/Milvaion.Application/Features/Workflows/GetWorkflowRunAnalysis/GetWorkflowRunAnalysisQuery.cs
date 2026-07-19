using Milvaion.Application.Dtos.WorkflowDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Workflows.GetWorkflowRunAnalysis;

/// <summary>
/// Query to get a workflow run flattened for reading rather than for drawing.
/// </summary>
/// <remarks>
/// Same underlying data as <see cref="GetWorkflowRunDetail.GetWorkflowRunDetailQuery"/>. The two are kept apart
/// because the canvas needs the graph and everything else needs the narrative, and collapsing them would force one
/// of the two to work around the other's shape.
/// </remarks>
public record GetWorkflowRunAnalysisQuery : IQuery<WorkflowRunAnalysisDto>
{
    /// <summary>
    /// Workflow run ID.
    /// </summary>
    public Guid RunId { get; set; }

    /// <summary>
    /// Maximum characters of a step's output to return. Zero omits outputs entirely.
    /// </summary>
    /// <remarks>
    /// A step passing a large payload downstream can carry hundreds of kilobytes of JSON, and a run has many
    /// steps. Truncating per step keeps one slow pipeline from producing a response nobody can use.
    /// </remarks>
    public int MaxOutputLength { get; set; } = 2000;
}
