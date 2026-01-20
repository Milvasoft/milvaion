using FluentValidation;

namespace Milvaion.Application.Features.Workers.GetWorkerList;

/// <summary>
/// Account detail query validations.
/// </summary>
public sealed class GetWorkerListQueryValidator : AbstractValidator<GetWorkerListQuery>
{
    ///<inheritdoc cref="GetWorkerListQueryValidator"/>
    public GetWorkerListQueryValidator()
    {
    }
}