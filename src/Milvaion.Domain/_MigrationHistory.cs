using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Milvaion.Domain;

/// <summary>
/// Entity of the MigrationHistory table.
/// </summary>
[Table(TableNames.MigrationHistory)]
public class MigrationHistory
{
    /// <summary>
    /// Id of migration
    /// </summary>
    [Key]
    public string MigrationId { get; set; }

    /// <summary>
    /// Migration fully completed or not.
    /// </summary>
    public bool MigrationCompleted { get; set; }
}

/// <summary>
/// Entity of the MigrationHistory table.
/// </summary>
[Table(TableNames.EfMigrationHistory)]
[NotMapped]
public class EfMigrationHistory
{
    /// <summary>
    /// Id of migration
    /// </summary>
    [Key]
    public string MigrationId { get; set; }
}
