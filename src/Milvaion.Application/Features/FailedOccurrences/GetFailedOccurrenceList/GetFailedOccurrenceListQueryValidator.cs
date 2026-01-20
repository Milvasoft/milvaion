using FluentValidation;

namespace Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetFailedOccurrenceListQueryValidator : AbstractValidator<GetFailedOccurrenceListQuery>
{
    ///<inheritdoc cref="GetFailedOccurrenceListQueryValidator"/>
    public GetFailedOccurrenceListQueryValidator()
    {
    }
}