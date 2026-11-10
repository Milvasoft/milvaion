namespace Milvaion.Application.Interfaces;

/// <summary>
/// Service for controlling the job dispatcher at runtime (emergency stop/resume).
/// </summary>
public interface IDispatcherControlService
{
    /// <summary>
    /// Gets whether the dispatcher is currently enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Stops the dispatcher with a reason.
    /// </summary>
    /// <param name="reason">Reason for stopping</param>
    /// <param name="username">Username who requested the stop</param>
    void Stop(string reason, string username);

    /// <summary>
    /// Resumes the dispatcher.
    /// </summary>
    /// <param name="username">Username who requested the resume</param>
    void Resume(string username);

    /// <summary>
    /// Gets the current stop reason if dispatcher is stopped.
    /// </summary>
    string GetStopReason();

    /// <summary>
    /// Picks up a stop or resume requested through another API instance.
    /// Keeps the last known state when the shared store cannot be read.
    /// </summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}