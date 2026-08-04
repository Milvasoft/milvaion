using Milvaion.Domain.JsonModels;
using Milvasoft.Core.EntityBases.Concrete.Auditing;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Single-row table holding the application's runtime settings as one jsonb document.
///
/// There is exactly one row. <c>ISettingsProvider</c> reads it (cached in memory), and an
/// update invalidates that cache across every instance via Redis pub/sub, so a change takes
/// effect at runtime without a restart. The document is jsonb, so changing the settings model
/// never requires a migration.
/// </summary>
[Table(TableNames.AppSettings)]
public class AppSetting : AuditableEntity<int>
{
    /// <summary>
    /// The settings document. Stored as jsonb; the whole object is (de)serialized by Npgsql.
    /// </summary>
    [Column(TypeName = "jsonb")]
    public AppSettingsDocument Document { get; set; } = new();
}
