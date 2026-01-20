using Milvaion.Application.Dtos.ConfigurationDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.Configuration.GetSystemConfiguration;

/// <summary>
/// Query for getting system configuration.
/// </summary>
public record GetSystemConfigurationQuery : IQuery<SystemConfigurationDto>
{
}
