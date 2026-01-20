using Milvasoft.Attributes.Annotations;
using Milvasoft.Milvaion.Sdk.Models;
using System.Linq.Expressions;
using System.Text.Json.Serialization;

namespace Milvaion.Application.Dtos.WorkerDtos;

/// <summary>
/// Worker list DTO for UI.
/// Maps from CachedWorker (Redis model), not MilvaionWorker (DB entity).
/// </summary>
public class WorkerDto
{
    /// <summary>
    /// Worker unique identifier.
    /// </summary>
    public string WorkerId { get; set; }

    /// <summary>
    /// Display name of the worker.
    /// </summary>
    public string DisplayName { get; set; }

    /// <summary>
    /// Routing patterns supported by the worker.
    /// </summary>
    public Dictionary<string, string> RoutingPatterns { get; set; }

    /// <summary>
    /// Job name and jobdata definition pair this worker handles.
    /// </summary>
    public Dictionary<string, string> JobDataDefinitions { get; set; } = [];

    /// <summary>
    /// Job names that the worker can execute.
    /// </summary>
    public List<string> JobNames { get; set; }

    /// <summary>
    /// Current number of jobs being processed by the worker.
    /// </summary>
    public int CurrentJobs { get; set; }

    /// <summary>
    /// Status of the worker (e.g., Online, Offline).
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the most recent successful heartbeat received from the monitored source.
    /// </summary>
    public DateTime? LastHeartbeat { get; set; }

    /// <summary>
    /// Registration timestamp of the worker.
    /// </summary>
    public DateTime RegisteredAt { get; set; }

    /// <summary>
    /// Version of the worker software.
    /// </summary>
    public string Version { get; set; }

    /// <summary>
    /// Metadata associated with the worker in JSON format.
    /// </summary>
    public string Metadata { get; set; }

    /// <summary>
    /// Active worker instances (replicas).
    /// </summary>
    public List<WorkerInstance> Instances { get; set; } = [];

    /// <summary>
    /// Projection expression for mapping CachedWorker to WorkerDto.
    /// </summary>
    [JsonIgnore]
    [ExcludeFromMetadata]
    public static Expression<Func<CachedWorker, WorkerDto>> Projection { get; } = r => new WorkerDto
    {
        WorkerId = r.WorkerId,
        DisplayName = r.DisplayName,
        RoutingPatterns = r.RoutingPatterns,
        JobDataDefinitions = r.JobDataDefinitions,
        JobNames = r.JobNames,
        CurrentJobs = r.CurrentJobs,
        Status = r.Status.ToString(),
        LastHeartbeat = r.LastHeartbeat,
        RegisteredAt = r.RegisteredAt,
        Version = r.Version,
        Metadata = r.Metadata,
        Instances = r.Instances
    };
}
