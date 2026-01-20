using Microsoft.EntityFrameworkCore;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq.Expressions;

namespace Milvaion.Domain.ContentManagement;

/// <summary>
/// Resource group of resources.
/// </summary>
[Table(TableNames.ResourceGroups)]
[Index(nameof(Slug))]
[Index(nameof(NamespaceId), nameof(Slug), IsUnique = true)]
public class ResourceGroup : AuditableEntity<int>
{
    /// <summary>
    /// Unique slug of resource group.
    /// </summary>
    public string Slug { get; set; }

    /// <summary>
    /// Name of resource group.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of resource group.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Id of the namespace this resource group belongs to.
    /// </summary>
    [ForeignKey(nameof(Namespace))]
    public int NamespaceId { get; set; }

    /// <summary>
    /// Navigation property to the namespace this resource group belongs to.
    /// </summary>
    public virtual Namespace Namespace { get; set; }

    /// <summary>
    /// Navigation property to the contents.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<Content> Contents { get; set; }

    #region Projections
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public static class Projections
    {
        public static Expression<Func<ResourceGroup, ResourceGroup>> CreateContent { get; } = rg => new ResourceGroup
        {
            Id = rg.Id,
            Slug = rg.Slug,
            NamespaceId = rg.NamespaceId,
            Namespace = new Namespace
            {
                Id = rg.Namespace.Id,
                Slug = rg.Namespace.Slug
            }
        };

    }

    #endregion
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}
