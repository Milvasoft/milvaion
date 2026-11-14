using FluentValidation;

namespace Milvaion.Application.Features.Workflows.CreateWorkflow;

/// <summary>
/// Validator for <see cref="CreateWorkflowCommand"/>.
/// </summary>
public sealed class CreateWorkflowCommandValidator : AbstractValidator<CreateWorkflowCommand>
{
    /// <inheritdoc cref="CreateWorkflowCommandValidator"/>
    public CreateWorkflowCommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Steps).NotEmpty().WithMessage("Workflow must have at least one step.");
        RuleFor(x => x.Steps)
            .Must(steps => steps == null || steps.Where(s => !string.IsNullOrWhiteSpace(s.TempId)).GroupBy(s => s.TempId).All(g => g.Count() == 1))
            .WithMessage("Each step must have a unique TempId.");
        RuleFor(x => x.TimeoutSeconds).GreaterThan(0).When(x => x.TimeoutSeconds.HasValue).WithMessage("TimeoutSeconds must be positive.");
        RuleFor(x => x.MaxStepRetries).GreaterThanOrEqualTo(0).WithMessage("MaxStepRetries cannot be negative.");
        RuleFor(x => x.MaxRetryAfter).GreaterThanOrEqualTo(0).WithMessage("MaxRetryAfter cannot be negative.");
        RuleFor(x => x.CronExpression)
            .Must(BeValidCronExpression)
            .When(x => !string.IsNullOrWhiteSpace(x.CronExpression))
            .WithMessage("CronExpression must be a valid 6-part cron format (second minute hour day month dayOfWeek).");
        RuleFor(x => x.Edges)
            .Must(edges => edges == null || edges.All(e => !string.IsNullOrWhiteSpace(e.SourceTempId) && !string.IsNullOrWhiteSpace(e.TargetTempId)))
            .WithMessage("Each edge must define source and target temp ids.");
        RuleForEach(x => x.Steps).ChildRules(step =>
        {
            step.RuleFor(s => s.StepName).NotEmpty().MaximumLength(200);
            step.RuleFor(s => s.TempId).NotEmpty().WithMessage("Each step must have a TempId for dependency referencing.");
            step.RuleFor(s => s.JobId)
                .NotEmpty()
                .When(s => s.NodeType == WorkflowNodeType.Task)
                .WithMessage("Task nodes must have a Job.");
        });
    }

    private static bool BeValidCronExpression(string cronExpression)
    {
        try
        {
            Cronos.CronExpression.Parse(cronExpression, Cronos.CronFormat.IncludeSeconds);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
