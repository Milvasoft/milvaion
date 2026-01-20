using FluentValidation;

namespace Milvaion.Application.Features.ScheduledJobs.GetJobOccurenceList;

/// <summary>
/// Account detail query validations. 
/// </summary>
public sealed class GetJobOccurrenceListQueryValidator : AbstractValidator<GetJobOccurrenceListQuery>
{
    ///<inheritdoc cref="GetJobOccurrenceListQueryValidator"/>
    public GetJobOccurrenceListQueryValidator()
    {
    }
}