using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvasoft.Components.CQRS.Query;

namespace Milvaion.Application.Features.ApiKeys.GetApiKeyDetail;

/// <summary>
/// Data transfer object for api key details.
/// </summary>
public record GetApiKeyDetailQuery : IQuery<ApiKeyDetailDto>
{
    /// <summary>
    /// Api key id to access details.
    /// </summary>
    public int ApiKeyId { get; set; }
}
