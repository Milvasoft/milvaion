using Cronos;
using FluentValidation;

namespace Milvaion.Application.Features.Workflows.UpdateWorkflow;

/// <summary>
/// Validator for <see cref="UpdateWorkflowCommand"/>.
/// </summary>
/// <remarks>
/// Every rule is gated on the field's <c>IsUpdated</c> flag. A partial update must not be rejected for a field it
/// never intended to change - otherwise renaming a workflow would fail because the caller did not resend the steps.
/// </remarks>
public sealed class UpdateWorkflowCommandValidator : AbstractValidator<UpdateWorkflowCommand>
{
    /// <inheritdoc cref="UpdateWorkflowCommandValidator"/>
    public UpdateWorkflowCommandValidator()
    {
        RuleFor(x => x.WorkflowId).NotEmpty();

        RuleFor(x => x.Name.Value)
            .NotEmpty()
            .MaximumLength(200)
            .When(x => x.Name.IsUpdated);

        RuleFor(x => x.TimeoutSeconds.Value)
            .GreaterThan(0)
            .When(x => x.TimeoutSeconds.IsUpdated && x.TimeoutSeconds.Value.HasValue)
            .WithMessage("TimeoutSeconds must be positive.");

        RuleFor(x => x.MaxStepRetries.Value)
            .GreaterThanOrEqualTo(0)
            .When(x => x.MaxStepRetries.IsUpdated)
            .WithMessage("MaxStepRetries cannot be negative.");

        RuleFor(x => x.CronExpression.Value)
            .MaximumLength(100)
            .Must(expr =>
            {
                if (string.IsNullOrWhiteSpace(expr))
                    return true;
                try
                {
                    CronExpression.Parse(expr, CronFormat.IncludeSeconds);
                    return true;
                }
                catch
                {
                    return false;
                }
            })
            .When(x => x.CronExpression.IsUpdated)
            .WithMessage("Invalid cron expression.");

        RuleFor(x => x.Steps.Value)
            .NotEmpty()
            .When(x => x.Steps.IsUpdated)
            .WithMessage("Workflow must have at least one step.");

        RuleFor(x => x.Edges.Value)
            .Must(edges => edges == null || edges.All(e => !string.IsNullOrWhiteSpace(e.SourceTempId) && !string.IsNullOrWhiteSpace(e.TargetTempId)))
            .When(x => x.Edges.IsUpdated)
            .WithMessage("Each edge must define source and target temp ids.");

        RuleForEach(x => x.Steps.Value)
            .ChildRules(step =>
            {
                step.RuleFor(s => s.StepName).NotEmpty().MaximumLength(200);
                step.RuleFor(s => s.TempId).NotEmpty().WithMessage("Each step must have a TempId for edge referencing.");
                step.RuleFor(s => s.JobId)
                    .NotEmpty()
                    .When(s => s.NodeType == WorkflowNodeType.Task)
                    .WithMessage("Task nodes must have a JobId.");
            })
            .When(x => x.Steps.IsUpdated);
    }
}
