using Mapster;
using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvasoft.Components.CQRS.Command;
using Milvasoft.Components.Rest.MilvaResponse;
using Milvasoft.Core.Abstractions;
using Milvasoft.Interception.Ef.Transaction;
using Milvasoft.Interception.Interceptors.Logging;

namespace Milvaion.Application.Features.ApiKeys.CreateApiKey;

/// <summary>
/// Handles the creation of the api key.
/// </summary>
/// <remarks>
/// The key embeds the record id, so the record must be persisted before the key can be generated. The record is
/// therefore saved first, then updated with the mask - both inside one transaction.
/// </remarks>
/// <param name="ApiKeyRepository"></param>
/// <param name="ApiKeyGenerator"></param>
[Log]
[Transaction]
[UserActivityTrack(UserActivity.CreateApiKey)]
public record CreateApiKeyCommandHandler(IMilvaionRepositoryBase<MilvaionApiKey> ApiKeyRepository,
                                         IApiKeyGenerator ApiKeyGenerator) : IInterceptable, ICommandHandler<CreateApiKeyCommand, CreatedApiKeyDto>
{
    private readonly IMilvaionRepositoryBase<MilvaionApiKey> _apiKeyRepository = ApiKeyRepository;
    private readonly IApiKeyGenerator _apiKeyGenerator = ApiKeyGenerator;

    /// <inheritdoc/>
    public async Task<Response<CreatedApiKeyDto>> Handle(CreateApiKeyCommand request, CancellationToken cancellationToken)
    {
        var apiKey = request.Adapt<MilvaionApiKey>();

        apiKey.KeyVersion = _apiKeyGenerator.CurrentKeyVersion;

        apiKey.ApiKeyPermissionRelations = request.PermissionIdList?.Distinct()
                                                                    .Select(permissionId => new MilvaionApiKeyPermissionRelation { PermissionId = permissionId })
                                                                    .ToList();

        await _apiKeyRepository.AddAsync(apiKey, cancellationToken);

        var generatedKey = _apiKeyGenerator.Generate(apiKey.Id, request.ExpiresAt);

        apiKey.MaskedKey = _apiKeyGenerator.Mask(generatedKey);

        await _apiKeyRepository.UpdateAsync(apiKey, cancellationToken);

        return Response<CreatedApiKeyDto>.Success(new CreatedApiKeyDto
        {
            Id = apiKey.Id,
            Name = apiKey.Name,
            Key = generatedKey,
            ExpiresAt = apiKey.ExpiresAt
        });
    }
}
