namespace Milvaion.Application.Interfaces.Secrets;

/// <summary>
/// Keeps the unresolved job data of dispatched occurrences that reference secrets.
/// </summary>
/// <remarks>
/// A worker that gives up on a job dead-letters the job as it received it, which is the resolved data. The
/// failed occurrence record stores the template from here instead, so its job data shows
/// <c>{{secret:name}}</c> and a retry from it resolves the secret again.
/// </remarks>
public interface ISecretTemplateStore
{
    /// <summary>
    /// Remembers <paramref name="template"/> for the occurrence. Failures are logged, not thrown.
    /// </summary>
    Task SaveAsync(Guid occurrenceId, string template, CancellationToken cancellationToken = default);

    /// <summary>
    /// The template saved for the occurrence, or <c>null</c>.
    /// </summary>
    Task<string> GetAsync(Guid occurrenceId, CancellationToken cancellationToken = default);
}
