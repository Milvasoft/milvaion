using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Request;

namespace Milvaion.Application.Features.ApiKeys.GetApiKeyList;

/// <summary>
/// Data transfer object for api key list.
/// </summary>
public record GetApiKeyListQuery : ListRequest, IListRequestQuery<ApiKeyListDto>
{
}
