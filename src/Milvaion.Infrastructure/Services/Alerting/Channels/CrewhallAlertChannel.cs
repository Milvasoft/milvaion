using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Utils.Constants;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Milvaion.Infrastructure.Services.Alerting.Channels;

/// <summary>
/// Alert channel implementation for Crewhall chat using incoming webhooks. The webhook URL carries its key, so it is a
/// secret: it is never logged or returned.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="CrewhallAlertChannel"/> class.
/// </remarks>
public class CrewhallAlertChannel(IOptions<AlertingOptions> alertingOptions,
                                  IHttpClientFactory httpClientFactory,
                                  Lazy<IMilvaLogger> logger) : AlertChannelBase<CrewhallChannelOptions>(alertingOptions)
{
    // Limits of Crewhall's AlertCard schema; one field over its limit rejects the whole message.
    private const int _titleMaxLength = 300;
    private const int _subtitleMaxLength = 300;
    private const int _sourceMaxLength = 100;
    private const int _textMaxLength = 4000;
    private const int _fieldValueMaxLength = 1000;

    private readonly HttpClient _httpClient = httpClientFactory.CreateClient(nameof(CrewhallAlertChannel));
    private readonly Lazy<IMilvaLogger> _logger = logger;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Keeps non-ASCII text at its UTF-8 size, so the largest card stays under the 32 KB body limit.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <inheritdoc/>
    public override string ChannelName => nameof(AlertChannelType.Crewhall);

    /// <inheritdoc/>
    protected override CrewhallChannelOptions GetChannelOptions(AlertingOptions options) => options.Channels?.Crewhall;

    /// <inheritdoc/>
    protected override async Task<ChannelResult> SendCoreAsync(AlertType alertType, AlertPayload payload, CancellationToken cancellationToken)
    {
        var chatConfig = _channelOptions.Chats?.FirstOrDefault(c => string.Equals(c.Chat, _channelOptions.DefaultChat, StringComparison.OrdinalIgnoreCase));

        if (chatConfig == null || !TryGetWebhookUri(chatConfig.WebhookUrl, out var webhookUri))
            return ChannelResult.Skipped(ChannelName, "No valid webhook URL configured for the default chat");

        var message = new CrewhallHookMessage { Card = CreateCard(alertType, payload) };

        using var content = new StringContent(JsonSerializer.Serialize(message, _jsonOptions), Encoding.UTF8, MimeTypeNames.ApplicationJson);

        using var response = await _httpClient.PostAsync(webhookUri, content, cancellationToken);

        if (response.IsSuccessStatusCode)
            return ChannelResult.Successful(ChannelName);

        var code = await ReadErrorCodeAsync(response, cancellationToken);

        _logger.Value.Warning("Crewhall alert failed. Status: {StatusCode}, Code: {Code}", response.StatusCode, code);

        return ChannelResult.Failed(ChannelName, $"HTTP {response.StatusCode}: {code}");
    }

    private static bool TryGetWebhookUri(string webhookUrl, out Uri webhookUri)
    {
        webhookUri = null;

        if (string.IsNullOrWhiteSpace(webhookUrl) || !Uri.TryCreate(webhookUrl.Trim(), UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        webhookUri = uri;

        return true;
    }

    /// <summary>
    /// Reads only Crewhall's error code; the rest of the body is not repeated.
    /// </summary>
    private async Task<string> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        try
        {
            return JsonSerializer.Deserialize<CrewhallErrorBody>(responseBody, _jsonOptions)?.Code ?? "unknown";
        }
        catch (JsonException)
        {
            return "unknown";
        }
    }

    private CrewhallAlertCard CreateCard(AlertType alertType, AlertPayload payload)
    {
        var fields = new List<CrewhallCardField>
        {
            new() { Label = "Severity", Value = payload.Severity.ToString() },
            new() { Label = "Time", Value = payload.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + " UTC" }
        };

        if (payload.AdditionalData != null)
        {
            var additionalDataJson = JsonSerializer.Serialize(payload.AdditionalData, _jsonOptions);

            if (additionalDataJson.Length <= _fieldValueMaxLength)
                fields.Add(new CrewhallCardField { Label = "Additional Data", Value = additionalDataJson });
        }

        return new CrewhallAlertCard
        {
            Severity = payload.Severity switch
            {
                AlertSeverity.Critical or AlertSeverity.Error => "critical",
                AlertSeverity.Warning => "warning",
                _ => "info"
            },
            Title = TruncateMessage(payload.Title ?? alertType.ToString(), _titleMaxLength),
            Subtitle = TruncateMessage(alertType.ToString(), _subtitleMaxLength),
            Source = TruncateMessage(string.IsNullOrWhiteSpace(payload.Source) ? "Milvaion" : payload.Source, _sourceMaxLength),
            Text = string.IsNullOrWhiteSpace(payload.Message) ? null : TruncateMessage(payload.Message, _textMaxLength),
            Fields = fields,
            Buttons = CreateButtons(payload.ActionLink)
        };
    }

    /// <summary>
    /// Crewhall accepts only absolute button addresses; a relative link (no MilvaionAppUrl configured) is left out.
    /// </summary>
    private static List<CrewhallCardButton> CreateButtons(string actionLink)
    {
        if (!Uri.TryCreate(actionLink, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return [];

        return [new CrewhallCardButton { Text = "View Details", Url = actionLink }];
    }

    private static string TruncateMessage(string message, int maxLength)
    {
        if (string.IsNullOrEmpty(message) || message.Length <= maxLength)
            return message;

        return message[..(maxLength - 3)] + "...";
    }
}

#region Crewhall Message Models

internal class CrewhallHookMessage
{
    public CrewhallAlertCard Card { get; set; }
}

internal class CrewhallAlertCard
{
    public string Severity { get; set; }

    public string Title { get; set; }

    public string Subtitle { get; set; }

    public string Source { get; set; }

    public string Text { get; set; }

    public List<CrewhallCardField> Fields { get; set; }

    public List<CrewhallCardButton> Buttons { get; set; }
}

internal class CrewhallCardField
{
    public string Label { get; set; }

    public string Value { get; set; }
}

internal class CrewhallCardButton
{
    public string Text { get; set; }

    public string Url { get; set; }
}

internal class CrewhallErrorBody
{
    public string Code { get; set; }
}

#endregion
