using Milvaion.Application.Dtos.FailedOccurrenceDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceDetail;

/// <summary>
/// Data transfer object for failedjob details.
/// </summary>
public record GetFailedOccurrenceDetailQuery : IQuery<FailedOccurrenceDetailDto>
{
    /// <summary>
    /// FailedOccurrence id to access details.
    /// </summary>
    public Guid FailedOccurrenceId { get; set; }
}
