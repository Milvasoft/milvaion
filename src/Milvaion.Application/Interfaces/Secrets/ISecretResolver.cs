namespace Milvaion.Application.Interfaces.Secrets;

/// <summary>
/// Replaces <c>{{secret:name}}</c> references in job data with their values.
/// </summary>
/// <remarks>
/// Resolution happens only at dispatch, on the copy of the job data that goes into the RabbitMQ message.
/// The resolved text must never be written back to the database, the cache, the logs or an API response.
/// </remarks>
public interface ISecretResolver
{
    /// <summary>
    /// Returns <paramref name="text"/> with every secret reference replaced by its value. Values are
    /// JSON-escaped, so the result stays valid JSON when the input was.
    /// </summary>
    /// <param name="text">Job data (normally a JSON document).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved text. The input is returned unchanged when it holds no references.</returns>
    /// <exception cref="SecretResolutionException">One or more references could not be resolved.</exception>
    Task<string> ResolveAsync(string text, CancellationToken cancellationToken = default);
}
