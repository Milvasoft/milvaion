using Microsoft.EntityFrameworkCore;
using Milvaion.Domain.JsonModels;
using Milvasoft.Attributes.Annotations;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using Milvasoft.Milvaion.Sdk.Domain;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// A project is the unit of isolation: it owns jobs, workflows, occurrences, failures, reports, calendars and api keys.
/// Every installation has the Default project (<see cref="ProjectDefaults.DefaultProjectId"/>). See docs/githubdocs/PROJECTS-DESIGN.md.
/// </summary>
[Table(TableNames.Projects)]
[Index(nameof(Slug), IsUnique = true)]
[DontIndexCreationDate]
public class Project : AuditableEntity<int>
{
    /// <summary>
    /// Lowercase identifier used in the <c>X-Milvaion-Project</c> header, in URLs and later in routing keys.
    /// Unique and never changed after creation.
    /// </summary>
    [Required]
    [MaxLength(40)]
    public string Slug { get; set; }

    /// <summary>
    /// Display name.
    /// </summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; }

    /// <summary>
    /// Optional description.
    /// </summary>
    [MaxLength(500)]
    public string Description { get; set; }

    /// <summary>
    /// True only for the Default project, which cannot be deleted or archived.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>
    /// When set, the project is read-only: requests other than reads are rejected.
    /// </summary>
    public DateTime? ArchivedAt { get; set; }

    /// <summary>
    /// Project level settings (notification rules, quotas, retention). Empty until those features ship.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public ProjectSettingsDocument Settings { get; set; } = new();

    /// <summary>
    /// Members of the project.
    /// </summary>
    [CascadeOnDelete]
    public virtual List<ProjectMembership> Memberships { get; set; }
}
