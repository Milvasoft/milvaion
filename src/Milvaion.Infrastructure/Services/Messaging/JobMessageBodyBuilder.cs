using Milvaion.Application.Interfaces.Secrets;
using System.Text;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Messaging;

/// <summary>
/// Serializes a job for its worker, the same way for every transport.
/// </summary>
public sealed class JobMessageBodyBuilder(ISecretResolver secretResolver = null, ISecretTemplateStore templateStore = null)
{
    private readonly ISecretResolver _secretResolver = secretResolver;
    private readonly ISecretTemplateStore _templateStore = templateStore;

    /// <summary>
    /// Serializer settings of job messages.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Serializes the job for the worker, with secret references in its job data resolved.
    /// </summary>
    /// <remarks>
    /// The job object itself is never changed: it may be the cached or tracked instance, and anything written
    /// to it could end up in Redis or the database. The resolved data exists only in the returned bytes.
    /// </remarks>
    /// <exception cref="SecretResolutionException">A referenced secret could not be resolved.</exception>
    public async Task<byte[]> BuildAsync(ScheduledJob job, Guid occurrenceId, CancellationToken cancellationToken)
    {
        if (_secretResolver == null || !SecretReferences.ContainsReference(job.JobData))
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(job, JsonOptions));

        var resolved = await _secretResolver.ResolveAsync(job.JobData, cancellationToken);

        if (_templateStore != null)
            await _templateStore.SaveAsync(occurrenceId, job.JobData, cancellationToken);

        var node = JsonSerializer.SerializeToNode(job, JsonOptions)!.AsObject();

        node["jobData"] = resolved;

        return Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions));
    }
}
