using System.Text;

namespace Milvasoft.Milvaion.Sdk.Utils.Kafka;

/// <summary>
/// Topic and consumer group names of the Kafka transport. Every SDK derives them the same way, see docs/githubdocs/KAFKA-TRANSPORT.md.
/// </summary>
public static class KafkaTopicNames
{
    /// <summary>
    /// Shared name stem of every Milvaion topic and group.
    /// </summary>
    public const string Root = "milvaion";

    /// <summary>Worker → API status updates.</summary>
    public const string Status = Root + ".status";

    /// <summary>Worker → API occurrence logs.</summary>
    public const string Logs = Root + ".logs";

    /// <summary>Worker → API instance heartbeats.</summary>
    public const string Heartbeats = Root + ".heartbeats";

    /// <summary>Worker → API registrations.</summary>
    public const string Registrations = Root + ".registrations";

    /// <summary>Dead letter topic: jobs that exhausted their retries or failed permanently.</summary>
    public const string DeadLetter = Root + ".dlq";

    /// <summary>External scheduler adapters → API: job registrations.</summary>
    public const string ExternalRegistrations = Root + ".external.registrations";

    /// <summary>External scheduler adapters → API: occurrences.</summary>
    public const string ExternalOccurrences = Root + ".external.occurrences";

    /// <summary>Consumer group shared by every API replica.</summary>
    public const string ApiGroup = Root + ".api";

    private const string _jobsStem = Root + ".jobs.";
    private const string _retrySuffix = ".retry";

    /// <summary>
    /// Lower-cases <paramref name="value"/> and replaces every character Kafka does not accept in a topic name with <c>_</c>.
    /// </summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);

        foreach (var c in value.ToLowerInvariant())
            builder.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '_');

        return builder.ToString();
    }

    /// <summary>
    /// Throws when <paramref name="prefix"/> holds a character Kafka does not accept in a topic name.
    /// </summary>
    public static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
            return;

        foreach (var c in prefix)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
                throw new ArgumentException($"Kafka topic prefix '{prefix}' contains '{c}'. Only letters, digits, '.', '_' and '-' are allowed.", nameof(prefix));
        }
    }

    /// <summary>
    /// Job topic of a worker: <c>{prefix}milvaion.jobs.{worker}</c>.
    /// </summary>
    public static string Jobs(string prefix, string workerId) => $"{prefix}{_jobsStem}{Sanitize(workerId)}";

    /// <summary>
    /// Retry delay topic of a worker: <c>{prefix}milvaion.jobs.{worker}.retry</c>.
    /// </summary>
    public static string Retry(string prefix, string workerId) => Jobs(prefix, workerId) + _retrySuffix;

    /// <summary>
    /// Consumer group of a worker's instances: <c>{prefix}milvaion.worker.{worker}</c>.
    /// </summary>
    public static string WorkerGroup(string prefix, string workerId) => $"{prefix}{Root}.worker.{Sanitize(workerId)}";

    /// <summary>
    /// Prefixed shared topic or group name.
    /// </summary>
    public static string WithPrefix(string prefix, string name) => $"{prefix}{name}";

    /// <summary>
    /// The worker id a job topic or retry topic belongs to (sanitized), or <see langword="null"/> for any other topic.
    /// </summary>
    public static string WorkerOfJobTopic(string prefix, string topic)
    {
        var stem = $"{prefix}{_jobsStem}";

        if (topic == null || !topic.StartsWith(stem, StringComparison.Ordinal))
            return null;

        var rest = topic[stem.Length..];

        return rest.EndsWith(_retrySuffix, StringComparison.Ordinal) ? rest[..^_retrySuffix.Length] : rest;
    }

    /// <summary>
    /// The worker id a job is dispatched to: the job's worker id, or else the first segment of its routing pattern.
    /// </summary>
    public static string ResolveWorkerId(string workerId, string routingPattern)
    {
        if (!string.IsNullOrWhiteSpace(workerId))
            return workerId;

        if (string.IsNullOrWhiteSpace(routingPattern))
            return null;

        var first = routingPattern.Split('.', 2)[0];

        return first is "*" or "#" or "" ? null : first;
    }

    /// <summary>
    /// Maps a RabbitMQ queue name to the Kafka topic that carries the same traffic, or <see langword="null"/> when there is none.
    /// </summary>
    public static string ForQueue(string prefix, string queueName) => queueName switch
    {
        WorkerConstant.Queues.StatusUpdates => WithPrefix(prefix, Status),
        WorkerConstant.Queues.WorkerLogs => WithPrefix(prefix, Logs),
        WorkerConstant.Queues.WorkerHeartbeat => WithPrefix(prefix, Heartbeats),
        WorkerConstant.Queues.WorkerRegistration => WithPrefix(prefix, Registrations),
        WorkerConstant.Queues.FailedOccurrences => WithPrefix(prefix, DeadLetter),
        WorkerConstant.Queues.ExternalJobRegistration => WithPrefix(prefix, ExternalRegistrations),
        WorkerConstant.Queues.ExternalJobOccurrence => WithPrefix(prefix, ExternalOccurrences),
        _ => null
    };

    /// <summary>
    /// Shared topics the API consumes. The API creates them at startup.
    /// </summary>
    public static IReadOnlyList<string> SharedTopics(string prefix) =>
    [
        WithPrefix(prefix, Status),
        WithPrefix(prefix, Logs),
        WithPrefix(prefix, Heartbeats),
        WithPrefix(prefix, Registrations),
        WithPrefix(prefix, DeadLetter),
        WithPrefix(prefix, ExternalRegistrations),
        WithPrefix(prefix, ExternalOccurrences),
    ];
}
