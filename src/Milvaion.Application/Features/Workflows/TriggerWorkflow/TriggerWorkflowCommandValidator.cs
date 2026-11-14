using FluentValidation;

namespace Milvaion.Application.Features.Workflows.TriggerWorkflow;

/// <summary>
/// Validator for <see cref="TriggerWorkflowCommand"/>.
/// </summary>
public sealed class TriggerWorkflowCommandValidator : AbstractValidator<TriggerWorkflowCommand>
{
    /// <inheritdoc cref="TriggerWorkflowCommandValidator"/>
    public TriggerWorkflowCommandValidator()
    {
        RuleFor(x => x.WorkflowId).NotEmpty();

        // A little slack for clock skew between the caller and the API; anything older is almost certainly a mistake.
        RuleFor(x => x.ExecuteAt)
            .Must(t => t.Value.ToUniversalTime() > DateTime.UtcNow.AddMinutes(-5))
            .When(x => x.ExecuteAt.HasValue)
            .WithMessage("ExecuteAt must not be in the past.");
    }
}
