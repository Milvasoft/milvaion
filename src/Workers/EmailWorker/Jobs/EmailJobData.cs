using Milvasoft.Milvaion.Sdk.Worker.Attributes;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EmailWorker.Jobs;

/// <summary>
/// Job data for sending emails.
/// </summary>
public class EmailJobData
{
    /// <summary>
    /// Configuration key for dynamic enum values (SMTP config names).
    /// </summary>
    public const string SmtpConfigsKey = "EmailConfig:SmtpConfigs";

    /// <summary>
    /// SMTP configuration name to use (from worker configuration).
    /// If not specified, uses the default configuration.
    /// </summary>
    [Description("SMTP configuration alias (configured in worker's appsettings.json)")]
    [DynamicEnum(SmtpConfigsKey)]
    public string ConfigName { get; set; }

    /// <summary>
    /// Recipient email addresses (required).
    /// </summary>
    [Required]
    [Description("Recipient email addresses")]
    public List<string> To { get; set; } = [];

    /// <summary>
    /// CC (Carbon Copy) email addresses.
    /// </summary>
    [Description("CC recipients")]
    public List<string> Cc { get; set; }

    /// <summary>
    /// BCC (Blind Carbon Copy) email addresses.
    /// </summary>
    [Description("BCC recipients (hidden from other recipients)")]
    public List<string> Bcc { get; set; }

    /// <summary>
    /// Email subject line.
    /// </summary>
    [Required]
    [Description("Email subject line")]
    public string Subject { get; set; }

    /// <summary>
    /// Email body content.
    /// </summary>
    [Required]
    [Description("Email body content")]
    public string Body { get; set; }

    /// <summary>
    /// Whether the body is HTML or plain text.
    /// </summary>
    [DefaultValue(false)]
    [Description("Set to true if body contains HTML")]
    public bool IsHtml { get; set; } = false;

    /// <summary>
    /// Sender email address (overrides default from config).
    /// </summary>
    [Description("Sender email address (overrides default)")]
    public string FromEmail { get; set; }

    /// <summary>
    /// Sender display name (overrides default from config).
    /// </summary>
    [Description("Sender display name (overrides default)")]
    public string FromName { get; set; }

    /// <summary>
    /// Reply-To email address.
    /// </summary>
    [Description("Reply-To email address")]
    public string ReplyTo { get; set; }

    /// <summary>
    /// Email priority level.
    /// </summary>
    [DefaultValue(EmailPriority.Normal)]
    [Description("Email priority: Low, Normal, or High")]
    public EmailPriority Priority { get; set; } = EmailPriority.Normal;

    /// <summary>
    /// File attachments as base64-encoded content.
    /// </summary>
    [Description("File attachments")]
    public List<EmailAttachment> Attachments { get; set; }

    /// <summary>
    /// Custom email headers.
    /// </summary>
    [Description("Custom email headers")]
    public Dictionary<string, string> CustomHeaders { get; set; }

    /// <summary>
    /// Request read receipt.
    /// </summary>
    [DefaultValue(false)]
    [Description("Request read receipt from recipient")]
    public bool RequestReadReceipt { get; set; } = false;

    /// <summary>
    /// Request delivery receipt.
    /// </summary>
    [DefaultValue(false)]
    [Description("Request delivery confirmation")]
    public bool RequestDeliveryReceipt { get; set; } = false;
}

/// <summary>
/// Email attachment data.
/// </summary>
public class EmailAttachment
{
    /// <summary>
    /// File name with extension.
    /// </summary>
    [Required]
    [Description("File name (e.g., 'report.pdf')")]
    public string FileName { get; set; }

    /// <summary>
    /// Base64-encoded file content.
    /// </summary>
    [Required]
    [Description("Base64-encoded file content")]
    public string ContentBase64 { get; set; }

    /// <summary>
    /// MIME content type (e.g., "application/pdf").
    /// If not specified, will be inferred from file extension.
    /// </summary>
    [Description("MIME type (e.g., 'application/pdf')")]
    public string ContentType { get; set; }

    /// <summary>
    /// Whether this attachment should be inline (for HTML emails).
    /// </summary>
    [DefaultValue(false)]
    [Description("Inline attachment for HTML (use cid:filename in body)")]
    public bool IsInline { get; set; } = false;

    /// <summary>
    /// Content ID for inline attachments (used in HTML as cid:contentId).
    /// </summary>
    [Description("Content ID for inline images (use in HTML as cid:value)")]
    public string ContentId { get; set; }
}

/// <summary>
/// Email priority levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EmailPriority
{
    Low,
    Normal,
    High
}
