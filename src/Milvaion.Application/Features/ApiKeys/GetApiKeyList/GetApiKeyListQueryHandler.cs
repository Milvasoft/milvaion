using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ApiKeys.GetApiKeyList;

/// <summary>
/// Handles the api key list operation.
/// </summary>
/// <param name="apiKeyRepository"></param>
public class GetApiKeyListQueryHandler(IMilvaionRepositoryBase<MilvaionApiKey> apiKeyRepository) : IInterceptable, IListQueryHandler<GetApiKeyListQuery, ApiKeyListDto>
{
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = apiKeyRepository;

    /// <inheritdoc/>
    public async Task<ListResponse<ApiKeyListDto>> Handle(GetApiKeyListQuery request, CancellationToken cancellationToken)
    {
        var response = await _apiKeyRepository.GetAllAsync(request, projection: ApiKeyListDto.Projection, cancellationToken: cancellationToken);

        return response;
    }
}
