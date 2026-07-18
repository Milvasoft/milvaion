using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvasoft.Components.CQRS.Query;
using Milvasoft.Components.Rest.Enums;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;

namespace Milvaion.Application.Features.ApiKeys.GetApiKeyDetail;

/// <summary>
/// Handles the api key detail operation.
/// </summary>
/// <param name="apiKeyRepository"></param>
public class GetApiKeyDetailQueryHandler(IMilvaionRepositoryBase<MilvaionApiKey> apiKeyRepository) : IInterceptable, IQueryHandler<GetApiKeyDetailQuery, ApiKeyDetailDto>
{
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = apiKeyRepository;

    /// <inheritdoc/>
    public async Task<Response<ApiKeyDetailDto>> Handle(GetApiKeyDetailQuery request, CancellationToken cancellationToken)
    {
        var apiKey = await _apiKeyRepository.GetByIdAsync(request.ApiKeyId, projection: ApiKeyDetailDto.Projection, cancellationToken: cancellationToken);

        if (apiKey == null)
            return Response<ApiKeyDetailDto>.Success(apiKey, MessageKey.ApiKeyNotFound, MessageType.Warning);

        return Response<ApiKeyDetailDto>.Success(apiKey);
    }
}
