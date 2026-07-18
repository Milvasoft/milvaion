using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ApiKeys.RevokeApiKey;

/// <summary>
/// Data transfer object for api key revocation.
/// </summary>
/// <remarks>
/// Revoking is preferred over deleting: the record stays behind so the audit trail still explains what the key
/// was and who created it. Deleting is available separately for housekeeping.
/// </remarks>
public record RevokeApiKeyCommand : ICommand<int>
{
    /// <summary>
    /// Id of the api key to be revoked.
    /// </summary>
    public int ApiKeyId { get; set; }
}
