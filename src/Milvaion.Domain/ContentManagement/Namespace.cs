using Microsoft.EntityFrameworkCore;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain.ContentManagement;

/// <summary>
/// Namespace of resources.
/// </summary>
[Table(TableNames.Namespaces)]
[Index(nameof(Slug), IsUnique = true)]
public class Namespace : AuditableEntity<int>
{
    /// <summary>
    /// Unique slug of namespace.
    /// </summary>
    public string Slug { get; set; }

    /// <summary>
    /// Name of namespace.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Description of namespace.
    /// </summary>
    public string Description { get; set; }

    /// <summary>
    /// Navigation property to the resource groups. 
    /// </summary>
    [CascadeOnDelete]
    public virtual List<ResourceGroup> ResourceGroups { get; set; }

    /// <summary>
    /// Navigation property to the contents. 
    /// </summary>
    public virtual List<Content> Contents { get; set; }
}
