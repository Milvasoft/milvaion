using Microsoft.Extensions.Options;
using Milvaion.Application.Dtos.AlertingDtos;
using Milvaion.Application.Utils.Constants;
using Milvasoft.Core.Abstractions;
using Milvasoft.Core.Helpers;
using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.Json;

namespace Milvaion.Infrastructure.Services.Alerting.Channels;

/// <summary>
/// Alert channel implementation for Email using SMTP.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="EmailAlertChannel"/> class.
/// </remarks>
public class EmailAlertChannel(IOptions<AlertingOptions> alertingOptions, Lazy<IMilvaLogger> logger) : AlertChannelBase<EmailChannelOptions>(alertingOptions)
{
    private readonly Lazy<IMilvaLogger> _logger = logger;

    /// <inheritdoc/>
    public override string ChannelName => nameof(AlertChannelType.Email);

    /// <inheritdoc/>
    protected override EmailChannelOptions GetChannelOptions(AlertingOptions options)
        => options.Channels?.Email;

    /// <inheritdoc/>
    public override bool CanSend()
    {
        if (!base.CanSend())
            return false;

        // Additional validation for email configuration
        return !string.IsNullOrWhiteSpace(_channelOptions.SmtpHost)
               && !string.IsNullOrWhiteSpace(_channelOptions.SenderEmail)
               && _channelOptions.DefaultRecipients?.Count > 0;
    }

    /// <inheritdoc/>
    protected override async Task<ChannelResult> SendCoreAsync(AlertType alertType, AlertPayload payload, CancellationToken cancellationToken)
    {
        if (_channelOptions.DefaultRecipients.IsNullOrEmpty())
            return ChannelResult.Skipped(ChannelName, "No recipients configured");

        using var smtpClient = CreateSmtpClient();
        using var mailMessage = CreateMailMessage(alertType, payload);

        try
        {
            await smtpClient.SendMailAsync(mailMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Value.Error(ex, "Failed to send email alert for {AlertType}", alertType);

            return ChannelResult.Failed(ChannelName, ex.Message);
        }

        return ChannelResult.Successful(ChannelName);
    }

    private SmtpClient CreateSmtpClient()
    {
        // Use SenderEmail for credentials, or fall back to From address
        var credentialEmail = string.IsNullOrWhiteSpace(_channelOptions.SenderEmail)
            ? _channelOptions.From
            : _channelOptions.SenderEmail;

        var client = new SmtpClient(_channelOptions.SmtpHost, _channelOptions.SmtpPort)
        {
            EnableSsl = _channelOptions.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(credentialEmail, _channelOptions.SenderPassword)
        };

        return client;
    }

    private MailMessage CreateMailMessage(AlertType alertType, AlertPayload payload)
    {
        var severityEmoji = GetSeverityEmoji(payload.Severity);
        var subject = $"{severityEmoji} [{payload.Severity}] {payload.Title ?? alertType.ToString()}";

        // Handle DisplayName - can be null/empty
        var displayName = string.IsNullOrWhiteSpace(_channelOptions.DisplayName)
            ? null
            : _channelOptions.DisplayName;

        var fromAddress = string.IsNullOrWhiteSpace(displayName)
            ? new MailAddress(_channelOptions.From)
            : new MailAddress(_channelOptions.From, displayName, Encoding.UTF8);

        var mailMessage = new MailMessage
        {
            From = fromAddress,
            Subject = subject,
            Body = CreateEmailBody(alertType, payload),
            IsBodyHtml = true,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
            HeadersEncoding = Encoding.UTF8
        };

        foreach (var recipient in _channelOptions.DefaultRecipients)
        {
            mailMessage.To.Add(recipient);
        }

        return mailMessage;
    }

    private static string CreateEmailBody(AlertType alertType, AlertPayload payload)
    {
        var severityColor = GetSeverityColor(payload.Severity);

        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html><head><style>");
        sb.AppendLine("body { font-family: Arial, sans-serif; margin: 20px; }");
        sb.AppendLine(".alert-card { border: 1px solid #ddd; border-radius: 8px; overflow: hidden; max-width: 600px; }");
        sb.AppendLine($".alert-header {{ background-color: {severityColor}; color: white; padding: 15px; }}");
        sb.AppendLine(".alert-body { padding: 20px; }");
        sb.AppendLine(".alert-field { margin-bottom: 15px; }");
        sb.AppendLine(".alert-field-label { font-weight: bold; color: #666; margin-bottom: 5px; }");
        sb.AppendLine(".alert-field-value { color: #333; }");
        sb.AppendLine(".code-block { background-color: #f5f5f5; padding: 10px; border-radius: 4px; font-family: monospace; font-size: 12px; overflow-x: auto; }");
        sb.AppendLine(".alert-footer { background-color: #f9f9f9; padding: 10px; text-align: center; font-size: 12px; color: #999; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<div class='alert-card'>");

        // Header
        sb.AppendLine("<div class='alert-header'>");
        sb.AppendLine($"<h2 style='margin: 0;'>{GetSeverityEmoji(payload.Severity)} {System.Net.WebUtility.HtmlEncode(payload.Title ?? alertType.ToString())}</h2>");
        sb.AppendLine($"<p style='margin: 5px 0 0 0; opacity: 0.9;'>Severity: {payload.Severity}</p>");
        sb.AppendLine("</div>");

        // Body
        sb.AppendLine("<div class='alert-body'>");

        AddField(sb, "Alert Type", alertType.ToString());
        AddField(sb, "Time", payload.Timestamp.ToString("yyyy-MM-dd HH:mm:ss") + " UTC");

        if (!string.IsNullOrWhiteSpace(payload.Source))
            AddField(sb, "Source", payload.Source);

        if (!string.IsNullOrWhiteSpace(payload.Message))
            AddField(sb, "Message", payload.Message);

        if (payload.AdditionalData != null)
        {
            var additionalDataJson = JsonSerializer.Serialize(payload.AdditionalData, ConstantJsonOptions.WriteIndented);
            sb.AppendLine("<div class='alert-field'>");
            sb.AppendLine("<div class='alert-field-label'>Additional Data</div>");
            sb.AppendLine($"<div class='code-block'><pre>{System.Web.HttpUtility.HtmlEncode(additionalDataJson)}</pre></div>");
            sb.AppendLine("</div>");
        }

        if (!string.IsNullOrWhiteSpace(payload.ActionLink))
        {
            sb.AppendLine($"<div class='alert-field'><a href='{payload.ActionLink}' style='display: inline-block; background-color: {severityColor}; color: white; padding: 10px 20px; text-decoration: none; border-radius: 4px;'>View Details</a></div>");
        }

        sb.AppendLine("</div>");

        // Footer
        sb.AppendLine("<div class='alert-footer'>Sent by Milvaion Alerting System</div>");

        sb.AppendLine("</div>");
        sb.AppendLine("</body></html>");

        return sb.ToString();
    }

    private static void AddField(StringBuilder sb, string label, string value)
    {
        sb.AppendLine("<div class='alert-field'>");
        sb.AppendLine($"<div class='alert-field-label'>{label}</div>");
        sb.AppendLine($"<div class='alert-field-value'>{System.Web.HttpUtility.HtmlEncode(value)}</div>");
        sb.AppendLine("</div>");
    }
}
