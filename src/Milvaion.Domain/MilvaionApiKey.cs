using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the MilvaionApiKeys table.
/// Represents a non-interactive credential used by external clients (CI pipelines, MCP clients, integrations)
/// to authenticate against the Milvaion API.
/// </summary>
/// <remarks>
/// The key itself is never persisted. The issued token is a signed JWT whose <c>jti</c> claim carries this
/// entity's id, so authentication is a signature check followed by a lookup of this record. That lookup is what
/// makes revocation, expiry and last-used tracking possible.
/// </remarks>
[Table(TableNames.MilvaionApiKeys)]
[DontIndexCreationDate]
public class MilvaionApiKey : FullAuditableEntity<int>
{
    /// <summary>
    /// Human readable name of the key. (e.g. "CI pipeline", "Claude Code - Bugra")
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; }

    /// <summary>
    /// Optional description explaining what this key is used for.
    /// </summary>
    [MaxLength(500)]
    public string Description { get; set; }

    /// <summary>
    /// Last characters of the issued key, kept only so a user can match a listed key against the one
    /// stored in their configuration. Never contains enough of the key to be usable.
    /// </summary>
    [MaxLength(20)]
    public string MaskedKey { get; set; }

    /// <summary>
    /// Version of the signing secret this key was issued with.
    /// Allows the signing secret to be rotated without invalidating every key at once.
    /// </summary>
    public int KeyVersion { get; set; }

    /// <summary>
    /// UTC expiry of the key. Null means the key does not expire.
    /// </summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>
    /// UTC time at which the key was revoked. Null means the key is still active.
    /// A revoked key is rejected even though its signature is still valid.
    /// </summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>
    /// UTC time the key was last used to authenticate a request.
    /// Useful for spotting keys that can safely be removed.
    /// </summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>
    /// Navigation property of permission relation. Determines what the key is allowed to do.
    /// A key with no permissions can authenticate but is authorized for nothing.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<MilvaionApiKeyPermissionRelation> ApiKeyPermissionRelations { get; set; }

    /// <summary>
    /// Gets the unique identifier of the entity.
    /// </summary>
    /// <returns></returns>
    public override object GetUniqueIdentifier() => Id;

    /// <summary>
    /// Returns this instance of "<see cref="Type"/>.Name <see cref="BaseEntity{TKey}"/>.Id" as string.
    /// </summary>
    /// <returns></returns>
    public override string ToString() => $"[{GetType().Name} {Id}]";
}
