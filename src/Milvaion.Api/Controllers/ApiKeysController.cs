using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvaion.Application.Features.ApiKeys.CreateApiKey;
using Milvaion.Application.Features.ApiKeys.DeleteApiKey;
using Milvaion.Application.Features.ApiKeys.GetApiKeyDetail;
using Milvaion.Application.Features.ApiKeys.GetApiKeyList;
using Milvaion.Application.Features.ApiKeys.RevokeApiKey;
using Milvaion.Application.Features.ApiKeys.UpdateApiKey;
using Milvaion.Application.Utils.Attributes;
using Milvaion.Domain.Enums;
using Milvasoft.Components.Rest.MilvaResponse;

namespace Milvaion.Api.Controllers;

/// <summary>
/// Api key endpoints.
/// </summary>
/// <remarks>
/// These endpoints are deliberately reachable with an interactive session only. Managing api keys with an api key
/// would let a leaked key mint replacements for itself and survive revocation.
/// </remarks>
[ApiController]
[Route(GlobalConstant.FullRoute)]
[ApiVersion(GlobalConstant.CurrentApiVersion)]
[ApiExplorerSettings(GroupName = "v1.0")]
[UserTypeAuth(UserType.Manager)]
public class ApiKeysController(IMediator mediator) : ControllerBase
{
    private readonly IMediator _mediator = mediator;

    /// <summary>
    /// Gets api keys.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ApiKeyManagement.List)]
    [HttpPatch]
    public Task<ListResponse<ApiKeyListDto>> GetApiKeysAsync(GetApiKeyListQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Gets api key according to api key id.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ApiKeyManagement.Detail)]
    [HttpGet("apiKey")]
    public Task<Response<ApiKeyDetailDto>> GetApiKeyAsync([FromQuery] GetApiKeyDetailQuery request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Adds api key. The generated key is returned in this response and nowhere else - it is not persisted and
    /// cannot be retrieved again.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ApiKeyManagement.Create)]
    [HttpPost("apiKey")]
    public Task<Response<CreatedApiKeyDto>> AddApiKeyAsync(CreateApiKeyCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Updates api key metadata and permissions. Only the fields that are sent as isUpdated true are updated.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ApiKeyManagement.Update)]
    [HttpPut("apiKey")]
    public Task<Response<int>> UpdateApiKeyAsync(UpdateApiKeyCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Revokes api key. The key stops working immediately, but the record is kept for the audit trail.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ApiKeyManagement.Revoke)]
    [HttpPost("apiKey/revoke")]
    public Task<Response<int>> RevokeApiKeyAsync(RevokeApiKeyCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);

    /// <summary>
    /// Removes api key along with its audit trail. Prefer revoking unless you are cleaning up.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellation"></param>
    /// <returns></returns>
    [Auth(PermissionCatalog.ApiKeyManagement.Delete)]
    [HttpDelete("apiKey")]
    public Task<Response<int>> RemoveApiKeyAsync([FromQuery] DeleteApiKeyCommand request, CancellationToken cancellation) => _mediator.Send(request, cancellation);
}
