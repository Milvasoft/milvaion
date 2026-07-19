using FluentValidation;

namespace Milvaion.Application.Features.Workflows.GetWorkflowRunAnalysis;

/// <summary>
/// Validator for <see cref="GetWorkflowRunAnalysisQuery"/>.
/// </summary>
public sealed class GetWorkflowRunAnalysisQueryValidator : AbstractValidator<GetWorkflowRunAnalysisQuery>
{
    /// <inheritdoc cref="GetWorkflowRunAnalysisQueryValidator"/>
    public GetWorkflowRunAnalysisQueryValidator()
    {
        RuleFor(x => x.RunId).NotEmpty();

        RuleFor(x => x.MaxOutputLength).GreaterThanOrEqualTo(0);
    }
}
