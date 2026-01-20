namespace Milvasoft.Milvaion.Sdk.Domain.Enums;

/// <summary>
/// Categorizes the type of failure for analysis and filtering.
/// </summary>
public enum FailureType
{
    /// <summary>
    /// Unknown failure type.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Job exceeded maximum retry attempts.
    /// </summary>
    MaxRetriesExceeded = 1,

    /// <summary>
    /// Job execution timeout.
    /// </summary>
    Timeout = 2,

    /// <summary>
    /// Worker crash during execution.
    /// </summary>
    WorkerCrash = 3,

    /// <summary>
    /// Invalid job data or configuration.
    /// </summary>
    InvalidJobData = 4,

    /// <summary>
    /// External dependency failure (API, database, etc.).
    /// </summary>
    ExternalDependencyFailure = 5,

    /// <summary>
    /// Unhandled exception in job code.
    /// </summary>
    UnhandledException = 6,

    /// <summary>
    /// Job cancelled by user or system.
    /// </summary>
    Cancelled = 7,

    /// <summary>
    /// Marked as zombie (stuck in Queued status).
    /// </summary>
    ZombieDetection = 8
}
