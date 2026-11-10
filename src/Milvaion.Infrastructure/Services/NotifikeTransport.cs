using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.NotificationDtos;
using Milvaion.Application.Interfaces;
using Milvaion.Application.Utils.Extensions;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Utils.Constants;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milvaion.Infrastructure.Services;

/// <summary>
/// Delivers e-mails and in-app notifications through Notifike's HTTP integration API.
/// </summary>
/// <param name="options"></param>
/// <param name="httpClientFactory"></param>
/// <param name="logger"></param>
public class NotifikeTransport(IOptions<NotifikeOptions> options,
                               IHttpClientFactory httpClientFactory,
                               Lazy<IMilvaLogger> logger) : INotifikeTransport
{
    private const string _emailPath = "api/v1.0/notifications/send";
    private const string _inAppPath = "api/v1.0/inApp/send";
    private const string _apiKeyHeader = "X-Api-Key";

    // Limits of the Notifike contract.
    private const int _maxRecipientsPerRequest = 1000;
    private const int _subjectMaxLength = 500;
    private const int _inAppKeyMaxLength = 100;
    private const int _inAppBodyMaxLength = 4000;
    private const int _inAppLinkMaxLength = 1000;
    private const int _inAppDataMaxLength = 16_000;

    private const byte _emailChannel = 1;
    private const byte _normalPriority = 5;
    private const byte _highPriority = 9;

    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly NotifikeOptions _options = options.Value;
    private readonly Uri _baseUri = TryGetBaseUri(options.Value.BaseUrl);
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(nameof(NotifikeTransport));
    private readonly Lazy<IMilvaLogger> _logger = logger;

    private bool IsActive => _options.Enabled
                             && _baseUri != null
                             && !string.IsNullOrWhiteSpace(_options.ApiKey)
                             && (!_options.SendOnlyInProduction || MilvaionExtensions.IsCurrentEnvProduction());

    /// <inheritdoc/>
    public bool CanSendEmail => IsActive && _options.UseForEmail;

    /// <inheritdoc/>
    public bool CanSendInApp => IsActive && _options.UseForInternalNotifications;

    /// <inheritdoc/>
    public async Task<NotifikeTransportResult> SendEmailAsync(NotifikeEmail email, CancellationToken cancellationToken)
    {
        if (!CanSendEmail)
            return new NotifikeTransportResult(false, "Notifike e-mail delivery is not active");

        var recipients = CleanKeys(email.Recipients, maxLength: int.MaxValue);

        if (recipients.Count == 0)
            return new NotifikeTransportResult(false, "No recipients");

        var subject = email.Subject?.ReplaceLineEndings(" ");

        foreach (var chunk in recipients.Chunk(_maxRecipientsPerRequest))
        {
            var request = new EmailRequest
            {
                Channel = _emailChannel,
                Recipients = [.. chunk.Select(to => new EmailRecipient { To = to })],
                Subject = Truncate(subject, _subjectMaxLength),
                Body = email.HtmlBody,
                Priority = email.IsHighPriority ? _highPriority : _normalPriority,
                Tags = string.IsNullOrWhiteSpace(email.Tag) ? ["milvaion"] : ["milvaion", email.Tag]
            };

            var result = await PostAsync(_emailPath, request, cancellationToken);

            if (!result.Success)
                return result;
        }

        return new NotifikeTransportResult(true);
    }

    /// <inheritdoc/>
    public async Task<NotifikeTransportResult> SendInAppAsync(NotifikeInAppNotification notification, CancellationToken cancellationToken)
    {
        if (!CanSendInApp)
            return new NotifikeTransportResult(false, "Notifike in-app delivery is not active");

        var userKeys = CleanKeys(notification.UserKeys, _inAppKeyMaxLength);

        if (userKeys.Count == 0)
            return new NotifikeTransportResult(false, "No recipients");

        var data = ParseData(notification.DataJson);

        foreach (var chunk in userKeys.Chunk(_maxRecipientsPerRequest))
        {
            var request = new InAppRequest
            {
                Type = Truncate(notification.Type, _inAppKeyMaxLength),
                Body = Truncate(notification.Text, _inAppBodyMaxLength),

                // A cut address or id would point at the wrong place: better none.
                Link = notification.Link?.Length <= _inAppLinkMaxLength ? notification.Link : null,
                EntityType = notification.EntityType,
                EntityId = notification.EntityId?.Length <= _inAppKeyMaxLength ? notification.EntityId : null,
                Data = data,
                Recipients = [.. chunk.Select(userKey => new InAppRecipient { UserKey = userKey })]
            };

            var result = await PostAsync(_inAppPath, request, cancellationToken);

            if (!result.Success)
                return result;
        }

        return new NotifikeTransportResult(true);
    }

    private async Task<NotifikeTransportResult> PostAsync<TBody>(string path, TBody body, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, path))
            {
                Content = new StringContent(JsonSerializer.Serialize(body, _jsonOptions), Encoding.UTF8, MimeTypeNames.ApplicationJson)
            };

            // Without validation so that a malformed key can never be echoed back in a FormatException message.
            request.Headers.TryAddWithoutValidation(_apiKeyHeader, _options.ApiKey.Trim());

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            var envelope = await ReadEnvelopeAsync(response, cancellationToken);

            if (response.IsSuccessStatusCode && envelope?.IsSuccess == true)
                return new NotifikeTransportResult(true);

            var firstMessage = envelope?.Messages?.FirstOrDefault();
            var reason = Truncate(firstMessage?.Message, 500) ?? "Notifike returned no details";

            _logger.Value.Warning("Notifike delivery failed. Status: {StatusCode}, Key: {Key}, Reason: {Reason}", response.StatusCode, firstMessage?.Key, reason);

            return new NotifikeTransportResult(false, $"HTTP {response.StatusCode}: {reason}");
        }
        catch (Exception ex)
        {
            _logger.Value.Warning("Notifike delivery failed: {Reason}", ex.Message);

            return new NotifikeTransportResult(false, ex.Message);
        }
    }

    private static async Task<ResponseEnvelope> ReadEnvelopeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            return JsonSerializer.Deserialize<ResponseEnvelope>(responseBody, _jsonOptions);
        }
        catch (JsonException)
        {
            // Not an envelope (a proxy error page, for example): reported by the caller with the HTTP status.
            return null;
        }
    }

    private static Uri TryGetBaseUri(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate($"{baseUrl.Trim().TrimEnd('/')}/", UriKind.Absolute, out var uri))
            return null;

        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    private static List<string> CleanKeys(IEnumerable<string> keys, int maxLength)
        => [.. (keys ?? []).Where(k => !string.IsNullOrWhiteSpace(k))
                           .Select(k => k.Trim())
                           .Where(k => k.Length <= maxLength)
                           .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static JsonElement? ParseData(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > _inAppDataMaxLength)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            return value;

        return value[..(maxLength - 3)] + "...";
    }

    #region Notifike Models

    private sealed class EmailRequest
    {
        public byte Channel { get; set; }

        public List<EmailRecipient> Recipients { get; set; }

        public string Subject { get; set; }

        public string Body { get; set; }

        public byte Priority { get; set; }

        public List<string> Tags { get; set; }
    }

    private sealed class EmailRecipient
    {
        public string To { get; set; }
    }

    private sealed class InAppRequest
    {
        public string Type { get; set; }

        public string Body { get; set; }

        public string Link { get; set; }

        public string EntityType { get; set; }

        public string EntityId { get; set; }

        public JsonElement? Data { get; set; }

        public List<InAppRecipient> Recipients { get; set; }
    }

    private sealed class InAppRecipient
    {
        public string UserKey { get; set; }
    }

    private sealed class ResponseEnvelope
    {
        public bool IsSuccess { get; set; }

        public List<ResponseMessage> Messages { get; set; }
    }

    private sealed class ResponseMessage
    {
        public string Key { get; set; }

        public string Message { get; set; }
    }

    #endregion
}
