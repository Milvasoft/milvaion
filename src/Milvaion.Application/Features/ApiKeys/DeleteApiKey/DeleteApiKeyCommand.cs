using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ApiKeys.DeleteApiKey;

/// <summary>
/// Data transfer object for api key deletion.
/// </summary>
public record DeleteApiKeyCommand : ICommand<int>
{
    /// <summary>
    /// Id of the api key to be deleted.
    /// </summary>
    public int ApiKeyId { get; set; }
}
