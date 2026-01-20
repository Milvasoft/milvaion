using Microsoft.EntityFrameworkCore;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain.ContentManagement;

/// <summary>
/// Key value pairs.
/// </summary>
[Table(TableNames.Contents)]
[Index(nameof(LanguageId), nameof(KeyAlias), IsUnique = true)]
public class Content : AuditableEntity<int>
{
    /// <summary>
    /// Key of content.
    /// </summary>
    public string Key { get; set; }

    /// <summary>
    /// Value of content.
    /// </summary>
    public string Value { get; set; }

    /// <summary>
    /// LanguageId of content.
    /// </summary>
    public int LanguageId { get; set; }

    /// <summary>
    /// Slug for the namespace for search without join.
    /// </summary>
    public string NamespaceSlug { get; set; }

    /// <summary>
    /// Slug for the resource group for search without join.
    /// </summary>
    public string ResourceGroupSlug { get; set; }

    /// <summary>
    /// Key alias for search without join. A combination of namespace slug, resource group slug and key. etc. "namespaceSlug.resourceGroupSlug.key"
    /// </summary>
    public string KeyAlias { get; set; }

    /// <summary>
    /// Id of the namespace this content belongs to.
    /// </summary>
    [ForeignKey(nameof(Namespace))]
    public int NamespaceId { get; set; }

    /// <summary>
    /// Navigation property to the namespace this content belongs to.
    /// </summary>
    public virtual Namespace Namespace { get; set; }

    /// <summary>
    /// Id of the namespace this content belongs to.
    /// </summary>
    [ForeignKey(nameof(ResourceGroup))]
    public int ResourceGroupId { get; set; }

    /// <summary>
    /// Navigation property to the resource group this content belongs to.
    /// </summary>
    public virtual ResourceGroup ResourceGroup { get; set; }

    /// <summary>
    /// Navigation property to the medias.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<Media> Medias { get; set; }

    /// <summary>
    /// Builds key alias.
    /// </summary>
    /// <returns></returns>
    public string BuildKeyAlias() => $"{NamespaceSlug}.{ResourceGroupSlug}.{Key}";
}
