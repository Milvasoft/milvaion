using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ApiKeys.DeleteApiKey;

/// <summary>
/// Handles the deletion of the api key.
/// </summary>
/// <param name="ApiKeyRepository"></param>
/// <param name="ApiKeyCacheInvalidator"></param>
[Log]
[UserActivityTrack(UserActivity.DeleteApiKey)]
public record DeleteApiKeyCommandHandler(IMilvaionRepositoryBase<MilvaionApiKey> ApiKeyRepository,
                                         IApiKeyCacheInvalidator ApiKeyCacheInvalidator) : IInterceptable, ICommandHandler<DeleteApiKeyCommand, int>
{
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = ApiKeyRepository;
    private readonly IApiKeyCacheInvalidator _apiKeyCacheInvalidator = ApiKeyCacheInvalidator;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(DeleteApiKeyCommand request, CancellationToken cancellationToken)
    {
        var apiKey = await _apiKeyRepository.GetForDeleteAsync(request.ApiKeyId, cancellationToken: cancellationToken);

        if (apiKey == null)
            return Response<int>.Error(0, MessageKey.ApiKeyNotFound);

        await _apiKeyRepository.DeleteAsync(apiKey, cancellationToken: cancellationToken);

        await _apiKeyCacheInvalidator.InvalidateAsync(request.ApiKeyId, cancellationToken);

        return Response<int>.Success(request.ApiKeyId);
    }
}
