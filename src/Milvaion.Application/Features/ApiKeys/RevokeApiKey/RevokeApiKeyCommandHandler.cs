using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ApiKeys.RevokeApiKey;

/// <summary>
/// Handles the revocation of the api key.
/// </summary>
/// <param name="ApiKeyRepository"></param>
/// <param name="ApiKeyCacheInvalidator"></param>
[Log]
[UserActivityTrack(UserActivity.RevokeApiKey)]
public record RevokeApiKeyCommandHandler(IMilvaionRepositoryBase<MilvaionApiKey> ApiKeyRepository,
                                         IApiKeyCacheInvalidator ApiKeyCacheInvalidator) : IInterceptable, ICommandHandler<RevokeApiKeyCommand, int>
{
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = ApiKeyRepository;
    private readonly IApiKeyCacheInvalidator _apiKeyCacheInvalidator = ApiKeyCacheInvalidator;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(RevokeApiKeyCommand request, CancellationToken cancellationToken)
    {
        var apiKey = await _apiKeyRepository.GetByIdAsync(request.ApiKeyId, cancellationToken: cancellationToken);

        if (apiKey == null)
            return Response<int>.Error(0, MessageKey.ApiKeyNotFound);

        if (apiKey.RevokedAt.HasValue)
            return Response<int>.Error(0, MessageKey.ApiKeyAlreadyRevoked);

        apiKey.RevokedAt = DateTime.UtcNow;

        await _apiKeyRepository.UpdateAsync(apiKey, cancellationToken);

        // Without this the key would keep working until its cache entry expires, which is not what anyone
        // revoking a key expects.
        await _apiKeyCacheInvalidator.InvalidateAsync(request.ApiKeyId, cancellationToken);

        return Response<int>.Success(request.ApiKeyId);
    }
}
