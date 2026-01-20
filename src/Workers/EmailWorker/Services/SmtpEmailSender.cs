using EmailWorker.Jobs;
using EmailWorker.Options;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using System.Diagnostics;

namespace EmailWorker.Services;

/// <summary>
/// SMTP-based email sender using MailKit.
/// </summary>
public class SmtpEmailSender(IOptions<EmailWorkerOptions> options) : IEmailSender
{
    private readonly EmailWorkerOptions _options = options.Value;

    /// <inheritdoc/>
    public bool ConfigurationExists(string configName)
    {
        var name = configName ?? _options.DefaultConfigName;
        return _options.SmtpConfigs.ContainsKey(name);
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetAvailableConfigNames() => _options.GetConfigNames();

    /// <inheritdoc/>
    public async Task<EmailSendResult> SendEmailAsync(string configName, EmailJobData emailData, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        // Get SMTP configuration
        var config = GetSmtpConfig(configName);

        if (config == null)
        {
            return EmailSendResult.Failed($"SMTP configuration '{configName ?? _options.DefaultConfigName}' not found. Available: {string.Join(", ", GetAvailableConfigNames())}");
        }

        try
        {
            // Build the email message
            var message = BuildMimeMessage(emailData, config);

            // Send via SMTP
            using var client = new SmtpClient();

            // Configure SSL certificate validation
            if (config.IgnoreCertificateErrors)
            {
                client.ServerCertificateValidationCallback = (s, c, h, e) => true;
            }

            // Connect to SMTP server
            var secureSocketOptions = config.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None;

            // Auto-detect based on port if not explicitly configured
            if (config.Port == 465)
            {
                secureSocketOptions = SecureSocketOptions.SslOnConnect;
            }

            await client.ConnectAsync(config.Host, config.Port, secureSocketOptions, cancellationToken);

            // Authenticate if credentials provided
            if (!string.IsNullOrEmpty(config.Username))
            {
                await client.AuthenticateAsync(config.Username, config.Password, cancellationToken);
            }

            // Send the message
            var response = await client.SendAsync(message, cancellationToken);

            // Disconnect
            await client.DisconnectAsync(true, cancellationToken);

            stopwatch.Stop();

            var recipientCount = emailData.To.Count +
                                 (emailData.Cc?.Count ?? 0) +
                                 (emailData.Bcc?.Count ?? 0);

            return EmailSendResult.Succeeded(message.MessageId, recipientCount, stopwatch.ElapsedMilliseconds);
        }
        catch (AuthenticationException ex)
        {
            return EmailSendResult.Failed($"SMTP authentication failed: {ex.Message}");
        }
        catch (SmtpCommandException ex)
        {
            return EmailSendResult.Failed($"SMTP server error ({ex.StatusCode}): {ex.Message}");
        }
        catch (SmtpProtocolException ex)
        {
            return EmailSendResult.Failed($"SMTP protocol error: {ex.Message}");
        }
        catch (Exception ex)
        {
            return EmailSendResult.Failed($"Failed to send email: {ex.Message}");
        }
    }

    private SmtpConfig GetSmtpConfig(string configName)
    {
        var name = configName ?? _options.DefaultConfigName;

        if (_options.SmtpConfigs.TryGetValue(name, out var config))
            return config;

        return null;
    }

    private static MimeMessage BuildMimeMessage(EmailJobData emailData, SmtpConfig config)
    {
        var message = new MimeMessage();

        // Set sender
        var fromEmail = emailData.FromEmail ?? config.DefaultFromEmail;
        var fromName = emailData.FromName ?? config.DefaultFromName;
        message.From.Add(new MailboxAddress(fromName ?? fromEmail, fromEmail));

        // Set recipients
        foreach (var to in emailData.To)
        {
            message.To.Add(MailboxAddress.Parse(to));
        }

        // Add CC recipients
        if (emailData.Cc?.Count > 0)
        {
            foreach (var cc in emailData.Cc)
            {
                message.Cc.Add(MailboxAddress.Parse(cc));
            }
        }

        // Add BCC recipients
        if (emailData.Bcc?.Count > 0)
        {
            foreach (var bcc in emailData.Bcc)
            {
                message.Bcc.Add(MailboxAddress.Parse(bcc));
            }
        }

        // Set Reply-To
        if (!string.IsNullOrEmpty(emailData.ReplyTo))
        {
            message.ReplyTo.Add(MailboxAddress.Parse(emailData.ReplyTo));
        }

        // Set subject
        message.Subject = emailData.Subject;

        // Set priority
        message.Priority = emailData.Priority switch
        {
            EmailPriority.High => MessagePriority.Urgent,
            EmailPriority.Low => MessagePriority.NonUrgent,
            _ => MessagePriority.Normal
        };

        // Add custom headers
        if (emailData.CustomHeaders?.Count > 0)
        {
            foreach (var (key, value) in emailData.CustomHeaders)
            {
                message.Headers.Add(key, value);
            }
        }

        // Request receipts
        if (emailData.RequestReadReceipt && !string.IsNullOrEmpty(fromEmail))
        {
            message.Headers.Add("Disposition-Notification-To", fromEmail);
        }

        if (emailData.RequestDeliveryReceipt && !string.IsNullOrEmpty(fromEmail))
        {
            message.Headers.Add("Return-Receipt-To", fromEmail);
        }

        // Build body (with or without attachments)
        var builder = new BodyBuilder();

        if (emailData.IsHtml)
        {
            builder.HtmlBody = emailData.Body;
        }
        else
        {
            builder.TextBody = emailData.Body;
        }

        // Add attachments
        if (emailData.Attachments?.Count > 0)
        {
            foreach (var attachment in emailData.Attachments)
            {
                var bytes = Convert.FromBase64String(attachment.ContentBase64);
                var contentType = attachment.ContentType ?? MimeTypes.GetMimeType(attachment.FileName);

                if (attachment.IsInline)
                {
                    var inlineAttachment = builder.LinkedResources.Add(attachment.FileName, bytes, ContentType.Parse(contentType));

                    if (!string.IsNullOrEmpty(attachment.ContentId))
                    {
                        inlineAttachment.ContentId = attachment.ContentId;
                    }
                }
                else
                {
                    builder.Attachments.Add(attachment.FileName, bytes, ContentType.Parse(contentType));
                }
            }
        }

        message.Body = builder.ToMessageBody();

        return message;
    }
}
