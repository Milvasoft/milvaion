namespace Milvaion.Application.Interfaces.Secrets;

/// <summary>
/// Thrown when job data references a secret that could not be resolved. The occurrence fails and no
/// message is published.
/// </summary>
/// <remarks>
/// The message names the references, never values, so it is safe to store on the occurrence and show
/// on the dashboard.
/// </remarks>
public class SecretResolutionException : Exception
{
    /// <summary>
    /// The secret names that were not found in any provider.
    /// </summary>
    public IReadOnlyList<string> MissingSecrets { get; }

    /// <summary>
    /// Secret names whose provider failed (for example, the vault was unreachable), with a short reason.
    /// </summary>
    public IReadOnlyDictionary<string, string> FailedSecrets { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SecretResolutionException"/> class.
    /// </summary>
    public SecretResolutionException(IReadOnlyList<string> missingSecrets, IReadOnlyDictionary<string, string> failedSecrets)
        : base(BuildMessage(missingSecrets, failedSecrets))
    {
        MissingSecrets = missingSecrets ?? [];
        FailedSecrets = failedSecrets ?? new Dictionary<string, string>();
    }

    private static string BuildMessage(IReadOnlyList<string> missing, IReadOnlyDictionary<string, string> failed)
    {
        var parts = new List<string>();

        if (missing?.Count > 0)
            parts.Add($"secret(s) not found: {string.Join(", ", missing)}");

        if (failed?.Count > 0)
            parts.Add($"secret(s) could not be read: {string.Join(", ", failed.Select(f => $"{f.Key} ({f.Value})"))}");

        return $"Secret resolution failed, so the job was not published to a worker. Job data references {string.Join("; ", parts)}.";
    }
}
