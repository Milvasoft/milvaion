using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milvaion.Infrastructure.Services.Monitoring;

/// <summary>
/// Lightweight wrapper around the RabbitMQ Management HTTP API.
/// </summary>
/// <remarks>
/// Initializes a new instance of <see cref="RabbitMQManagementClient"/>.
/// </remarks>
public class RabbitMQManagementClient(IHttpClientFactory httpClientFactory, IOptions<RabbitMQOptions> options)
{
    internal const string _httpClientName = nameof(RabbitMQManagementClient);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
    private readonly RabbitMQOptions _options = options.Value;

    /// <summary>
    /// Gets statistics for a single queue from the Management API.
    /// Returns <see langword="null"/> when the Management API is disabled or the request fails.
    /// </summary>
    public async Task<ManagementQueueInfo> GetQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        if (!_options.ManagementEnabled)
            return null;

        try
        {
            var vhost = Uri.EscapeDataString(_options.VirtualHost);

            var queue = Uri.EscapeDataString(queueName);

            var client = CreateClient();

            var response = await client.GetAsync($"api/queues/{vhost}/{queue}", cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonSerializer.Deserialize<ManagementQueueInfo>(json, _jsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lists all queues in the configured virtual host from the Management API.
    /// Returns an empty collection when the Management API is disabled or the request fails.
    /// </summary>
    public async Task<IReadOnlyList<ManagementQueueInfo>> GetAllQueuesAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.ManagementEnabled)
            return [];

        try
        {
            var vhost = Uri.EscapeDataString(_options.VirtualHost);
            var client = CreateClient();
            var response = await client.GetAsync($"api/queues/{vhost}", cancellationToken);

            if (!response.IsSuccessStatusCode)
                return [];

            var json = await response.Content.ReadAsStringAsync(cancellationToken);

            return JsonSerializer.Deserialize<List<ManagementQueueInfo>>(json, _jsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient(_httpClientName);
        var baseUrl = $"http://{_options.Host}:{_options.ManagementPort}/";
        client.BaseAddress = new Uri(baseUrl);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.Username}:{_options.Password}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        return client;
    }
}

/// <summary>
/// Represents queue information returned by the RabbitMQ Management API.
/// </summary>
public sealed class ManagementQueueInfo
{
    /// <summary>Queue name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>Total messages in the queue.</summary>
    [JsonPropertyName("messages")]
    public uint Messages { get; init; }

    /// <summary>Messages ready for delivery.</summary>
    [JsonPropertyName("messages_ready")]
    public uint MessagesReady { get; init; }

    /// <summary>Messages unacknowledged by consumers.</summary>
    [JsonPropertyName("messages_unacknowledged")]
    public uint MessagesUnacknowledged { get; init; }

    /// <summary>Number of active consumers.</summary>
    [JsonPropertyName("consumers")]
    public uint Consumers { get; init; }
}
