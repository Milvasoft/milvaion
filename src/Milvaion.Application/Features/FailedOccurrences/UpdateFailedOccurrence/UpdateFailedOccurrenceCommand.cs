using Milvasoft.Components.CQRS.Command;
using Milvasoft.Core.EntityBases.Concrete;
using Milvasoft.Types.Structs;

namespace Milvaion.Application.Features.FailedOccurrences.UpdateFailedOccurrence;

/// <summary>
/// Data transfer object for failedjob update.
/// </summary>
public class UpdateFailedOccurrenceCommand : DtoBase, ICommand<List<Guid>>
{
    /// <summary>
    /// Ids of the failedjobs to be updated.
    /// </summary>
    public List<Guid> IdList { get; set; }

    /// <summary>
    /// Type of failure (for categorization and analysis).
    /// </summary>
    public UpdateProperty<FailureType> FailureType { get; set; }

    /// <summary>
    /// Indicates whether this failed job has been reviewed and resolved.
    /// </summary>
    public UpdateProperty<bool> Resolved { get; set; } = false;

    /// <summary>
    /// Timestamp when job was marked as resolved.
    /// </summary>
    public UpdateProperty<DateTime?> ResolvedAt { get; set; }

    /// <summary>
    /// Username who resolved this failed job.
    /// </summary>
    public UpdateProperty<string> ResolvedBy { get; set; }

    /// <summary>
    /// Resolution notes/comments.
    /// </summary>
    public UpdateProperty<string> ResolutionNote { get; set; }

    /// <summary>
    /// Action taken to resolve (e.g., "Retried manually", "Fixed data and re-queued", "Ignored - invalid data").
    /// </summary>
    public UpdateProperty<string> ResolutionAction { get; set; }

    /// <summary>
    /// Gets the unique identifier for the DTO.
    /// </summary>
    /// <returns></returns>
    public override object GetUniqueIdentifier() => IdList.FirstOrDefault();
}
