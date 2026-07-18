using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Milvaion.Api.Mcp;

/// <summary>
/// Prompt templates the user can pick explicitly, each one teaching the model a working order for a common task.
/// </summary>
/// <remarks>
/// The same guidance exists in <c>ServerInstructions</c>, but that is passive background the model may or may not
/// lean on. A prompt is chosen deliberately and puts the sequence directly in front of the model, which produces
/// far more consistent investigations than hoping it picks the right first tool.
/// </remarks>
[McpServerPromptType]
public static class MilvaionPrompts
{
    /// <summary>
    /// Walks through diagnosing a failing job.
    /// </summary>
    /// <param name="jobName">Name or id of the job to investigate.</param>
    /// <returns>The prompt text.</returns>
    [McpServerPrompt(Name = "diagnose_job")]
    [Description("Investigate why a specific job is failing, in a sensible order.")]
    public static string DiagnoseJob(
        [Description("Name or GUID id of the job that is failing.")] string jobName)
        => $"""
        Investigate why the job "{jobName}" is failing. Work in this order and stop as soon as the cause is clear:

        1. Find the job with list_jobs and note its id, schedule and assigned worker.
        2. Call list_failures with that jobId to see whether it has been reaching the dead letter queue, and how
           far back that goes.
        3. Call list_occurrences with the same jobId to see the pattern: is every run failing, or only some?
           Note the times - a job that only fails at a particular hour points somewhere different from one that
           fails every run.
        4. Pick a representative failure and call get_occurrence on it. Read the exception and the log tail.
           If the cause looks earlier in the run, call it again with a higher logLines.
        5. Check list_workers to confirm a healthy worker exists that can execute this job type. A job pointed at
           a dead or missing worker never runs at all, which looks different from a job that runs and throws.
        6. Call list_activity_logs bounded to shortly before the failures started. A job that used to work and
           now does not was often changed by someone.

        Then tell me what you found: what the failure actually is, when it started, whether anything changed
        around that time, and what you would do about it. Do not change anything yourself - propose, and I will
        decide.
        """;

    /// <summary>
    /// Reviews everything that failed in a recent window.
    /// </summary>
    /// <param name="hours">How many hours back to look.</param>
    /// <returns>The prompt text.</returns>
    [McpServerPrompt(Name = "overnight_review")]
    [Description("Review what failed recently and group it by likely cause.")]
    public static string OvernightReview(
        [Description("How many hours back to look. 12 covers a typical overnight window.")] int hours = 12)
        => $"""
        Review what went wrong in the last {hours} hours.

        1. Start with get_overview for the shape of things.
        2. Call list_failures with since set to {hours} hours ago, in UTC.
        3. Group the failures by likely cause rather than listing them one by one. Several jobs failing with the
           same exception in the same time window is one incident, not many - say so.
        4. For each group, call get_occurrence on one representative failure to get the actual exception.
        5. Call list_workers and note any worker whose heartbeat is stale. Failures concentrated on one worker
           mean something different from failures spread across all of them.

        Give me a short summary: how many distinct problems there are, what each one is, which jobs each affects,
        and which needs attention first. Lead with the conclusion, not the method.
        """;

    /// <summary>
    /// Explains what a workflow does.
    /// </summary>
    /// <param name="workflowName">Name or id of the workflow.</param>
    /// <returns>The prompt text.</returns>
    [McpServerPrompt(Name = "explain_workflow")]
    [Description("Explain a workflow's steps, branching and data flow in plain language.")]
    public static string ExplainWorkflow(
        [Description("Name or GUID id of the workflow.")] string workflowName)
        => $"""
        Explain what the workflow "{workflowName}" actually does.

        1. Find it with list_workflows, then call get_workflow for the full step graph.
        2. Walk the graph from its root steps and describe the flow in order. For condition nodes, say what the
           expression tests and where each of the true and false ports leads.
        3. Note any data mappings between steps - which output field of one step feeds which input of another.
        4. Call list_workflow_runs for this workflow to see how it behaves in practice, and whether particular
           steps fail more than others.

        Describe it as you would to someone who has to maintain it: what it is for, what runs in what order,
        where it can branch, and which parts look fragile.
        """;
}
