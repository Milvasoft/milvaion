using Milvaion.Application.Dtos.FailedOccurrenceDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.FailedOccurrences.GetFailedOccurrenceList;

/// <summary>
/// Data transfer object for failedjob list.
/// </summary>
public record GetFailedOccurrenceListQuery : ListRequest, IListRequestQuery<FailedOccurrenceListDto>
{
    /// <summary>
    /// Search term to failed job.
    /// </summary>
    public string SearchTerm { get; set; }
}