using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain.ContentManagement;

/// <summary>
/// Media typed content values.
/// </summary>
[Table(TableNames.Medias)]
public class Media : AuditableEntity<int>
{
    /// <summary>
    /// Value of media.
    /// </summary>
    public byte[] Value { get; set; }

    /// <summary>
    /// Media type. etc. "image", "video", "audio"
    /// </summary>
    public string Type { get; set; }

    /// <summary>
    /// Id of the content this media belongs to.
    /// </summary>
    [ForeignKey(nameof(Content))]
    public int ContentId { get; set; }

    /// <summary>
    /// Navigation property to the content this media to.
    /// </summary>
    public virtual Content Content { get; set; }
}
