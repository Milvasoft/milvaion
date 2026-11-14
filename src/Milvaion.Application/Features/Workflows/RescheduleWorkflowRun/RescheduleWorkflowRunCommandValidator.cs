using FluentValidation;

namespace Milvaion.Application.Features.Workflows.RescheduleWorkflowRun;

/// <summary>
/// Validator for <see cref="RescheduleWorkflowRunCommand"/>.
/// </summary>
public sealed class RescheduleWorkflowRunCommandValidator : AbstractValidator<RescheduleWorkflowRunCommand>
{
    /// <inheritdoc cref="RescheduleWorkflowRunCommandValidator"/>
    public RescheduleWorkflowRunCommandValidator()
    {
        RuleFor(x => x.WorkflowRunId).NotEmpty();

        RuleFor(x => x.ExecuteAt)
            .Must(t => t.ToUniversalTime() > DateTime.UtcNow.AddMinutes(-5))
            .WithMessage("ExecuteAt must not be in the past.");
    }
}
