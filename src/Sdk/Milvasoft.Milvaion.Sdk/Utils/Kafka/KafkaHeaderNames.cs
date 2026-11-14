using System.Globalization;
using System.Text;

namespace Milvasoft.Milvaion.Sdk.Utils.Kafka;

/// <summary>
/// Record header names of the Kafka transport. Values are UTF-8 strings; numbers are decimal strings.
/// </summary>
public static class KafkaHeaderNames
{
#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public const string OccurrenceId = "OccurrenceId";
    public const string CorrelationId = "CorrelationId";
    public const string JobType = "JobType";
    public const string WorkerId = "WorkerId";
    public const string ExecuteAt = "ExecuteAt";
    public const string Priority = "Priority";
    public const string RetryCount = "x-retry-count";
    public const string MaxRetries = "MaxRetries";
    public const string NotBefore = "x-not-before";
    public const string OriginalTopic = "x-original-topic";
    public const string DlqReason = "x-dlq-reason";
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
}

/// <summary>
/// Reads header values that may arrive as AMQP numbers, strings or raw bytes. Kafka sends every value as bytes;
/// RabbitMQ sends numbers as numbers and strings as bytes.
/// </summary>
public static class MessageHeaderValues
{
    /// <summary>
    /// The value as a string, or <see langword="null"/> when it is missing or not text.
    /// </summary>
    public static string GetString(IDictionary<string, object> headers, string name)
    {
        if (headers == null || !headers.TryGetValue(name, out var value) || value == null)
            return null;

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// The value as an integer, or <see langword="null"/> when it is missing or not a number.
    /// </summary>
    public static int? GetInt(IDictionary<string, object> headers, string name)
    {
        if (headers == null || !headers.TryGetValue(name, out var value) || value == null)
            return null;

        switch (value)
        {
            case int i:
                return i;
            case long l:
                return (int)l;
            case short s:
                return s;
            case byte b:
                return b;
            case byte[] or string or ReadOnlyMemory<byte>:
                return int.TryParse(GetString(headers, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                try
                {
                    return Convert.ToInt32(value, CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return null;
                }
        }
    }

    /// <summary>
    /// The value as a GUID, or <see langword="null"/>.
    /// </summary>
    public static Guid? GetGuid(IDictionary<string, object> headers, string name)
        => Guid.TryParse(GetString(headers, name), out var guid) ? guid : null;
}
