using Milvasoft.Milvaion.Sdk.Domain.Enums;

namespace Milvasoft.Milvaion.Sdk.Models;

public sealed class DlqJobMessage
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; }
    public string JobNameInWorker { get; set; }
    public string JobData { get; set; }
    public DateTime? ExecuteAt { get; set; }
    public JobOccurrenceStatus Status { get; set; }
    public string Exception { get; set; }
}