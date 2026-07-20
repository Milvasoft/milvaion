using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Configuration.GetSystemResourceUsage;

/// <summary>
/// Query for getting live CPU, memory and disk usage of the API host and process.
/// </summary>
/// <remarks>
/// Separate from <c>GetSystemConfigurationQuery</c> on purpose. That query returns resource usage too, but only
/// as one branch of a payload that also walks every configuration section, parses the connection strings and
/// enumerates the alerting channels. Asking it for a memory figure means paying for all of that, which rules it
/// out for anything that polls - the dashboard meters, an assistant answering "how much memory is it using".
/// This one reads the current process and nothing else.
/// </remarks>
public record GetSystemResourceUsageQuery : IQuery<SystemResourceUsageDto>
{
}
