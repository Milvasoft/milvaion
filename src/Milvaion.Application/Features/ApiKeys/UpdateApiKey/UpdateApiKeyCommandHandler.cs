using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ApiKeys.UpdateApiKey;

/// <summary>
/// Handles the update of the api key.
/// </summary>
/// <param name="ApiKeyRepository"></param>
/// <param name="ApiKeyPermissionRelationRepository"></param>
/// <param name="ApiKeyCacheInvalidator"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.UpdateApiKey)]
public record UpdateApiKeyCommandHandler(IMilvaionRepositoryBase<MilvaionApiKey> ApiKeyRepository,
                                         IMilvaionRepositoryBase<MilvaionApiKeyPermissionRelation> ApiKeyPermissionRelationRepository,
                                         IApiKeyCacheInvalidator ApiKeyCacheInvalidator) : IInterceptable, ICommandHandler<UpdateApiKeyCommand, int>
{
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = ApiKeyRepository;
    private readonly IMilvaionRepositoryBase<MilvaionApiKeyPermissionRelation> _apiKeyPermissionRelationRepository = ApiKeyPermissionRelationRepository;
    private readonly IApiKeyCacheInvalidator _apiKeyCacheInvalidator = ApiKeyCacheInvalidator;

    /// <inheritdoc/>
    public async Task<Response<int>> Handle(UpdateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var setPropertyBuilder = _apiKeyRepository.GetUpdatablePropertiesBuilder(request);

        await _apiKeyRepository.ExecuteUpdateAsync(request.Id, setPropertyBuilder, cancellationToken: cancellationToken);

        if (request.PermissionIdList.IsUpdated)
        {
            await _apiKeyPermissionRelationRepository.ExecuteDeleteAsync(rl => rl.MilvaionApiKeyId == request.Id, cancellationToken: cancellationToken);

            var addedEntities = request.PermissionIdList.Value?.Distinct()
                                                               .Select(permissionId => new MilvaionApiKeyPermissionRelation { MilvaionApiKeyId = request.Id, PermissionId = permissionId })
                                                               .ToList();

            if (addedEntities?.Count > 0)
                await _apiKeyPermissionRelationRepository.BulkAddAsync(addedEntities, null, cancellationToken);
        }

        // Authentication caches the key's permissions. Without this the old permission set stays in effect for
        // the remainder of the cache lifetime - so a permission you just removed would keep working, and one you
        // just granted would appear not to.
        await _apiKeyCacheInvalidator.InvalidateAsync(request.Id, cancellationToken);

        return Response<int>.Success(request.Id);
    }
}
