using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvaion.Application.Utils;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.Configuration.GetSystemResourceUsage;

/// <summary>
/// Handles the system resource usage query.
/// </summary>
/// <remarks>
/// Synchronous work wrapped in a completed task: the sample is taken from the current process, so there is
/// nothing to await. Making it look asynchronous would suggest an I/O cost that is not there.
/// </remarks>
public class GetSystemResourceUsageQueryHandler : IInterceptable, IQueryHandler<GetSystemResourceUsageQuery, SystemResourceUsageDto>
{
    /// <inheritdoc/>
    public Task<Response<SystemResourceUsageDto>> Handle(GetSystemResourceUsageQuery request, CancellationToken cancellationToken)
        => Task.FromResult(Response<SystemResourceUsageDto>.Success(SystemResourceReader.Read()));
}
