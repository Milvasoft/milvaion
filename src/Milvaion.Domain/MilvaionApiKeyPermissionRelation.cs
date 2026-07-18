using Milvasoft.Core.EntityBases.Concrete;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the MilvaionApiKeyPermissionRelations table.
/// </summary>
[Table(TableNames.MilvaionApiKeyPermissionRelations)]
public class MilvaionApiKeyPermissionRelation : BaseEntity<int>
{
    /// <summary>
    /// ID of the api key.
    /// </summary>
    [ForeignKey(nameof(MilvaionApiKey))]
    public int MilvaionApiKeyId { get; set; }

    /// <summary>
    /// ID of the permission.
    /// </summary>
    [ForeignKey(nameof(Permission))]
    public int PermissionId { get; set; }

    /// <summary>
    /// Navigation property of api key relation.
    /// </summary>
    public virtual MilvaionApiKey MilvaionApiKey { get; set; }

    /// <summary>
    /// Navigation property of Permission relation.
    /// </summary>
    public virtual Permission Permission { get; set; }
}
