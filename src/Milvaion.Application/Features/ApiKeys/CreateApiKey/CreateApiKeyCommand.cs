using Milvaion.Application.Dtos.ApiKeyDtos;
using Milvasoft.Components.CQRS.Command;

namespace Milvaion.Application.Features.ApiKeys.CreateApiKey;

/// <summary>
/// Data transfer object for api key creation.
/// </summary>
public record CreateApiKeyCommand : ICommand<CreatedApiKeyDto>
{
    /// <summary>
    /// Human readable name of the key. (e.g. "CI pipeline", "Claude Code - Bugra")
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Optional description explaining what the key is used for.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// UTC expiry of the key. Leave null for a key that never expires.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// Permissions to grant to the key. A key with no permissions can authenticate but is authorized for nothing.
    /// </summary>
    public List<int> PermissionIdList { get; set; }
}
